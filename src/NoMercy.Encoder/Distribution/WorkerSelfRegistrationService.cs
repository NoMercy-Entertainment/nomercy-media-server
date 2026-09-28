// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Encoder.Distribution;

internal sealed record RegisterPayload(
    [property: JsonPropertyName("worker_id")] string WorkerId,
    [property: JsonPropertyName("base_url")] string? BaseUrl,
    [property: JsonPropertyName("cpu_cores")] int CpuCores,
    [property: JsonPropertyName("available_cpu_threads")] int AvailableCpuThreads,
    [property: JsonPropertyName("available_gpu_slots")] int AvailableGpuSlots,
    [property: JsonPropertyName("gpus")] IReadOnlyList<GpuDevice> Gpus
);

internal sealed record HeartbeatPayload(
    [property: JsonPropertyName("available_cpu_threads")] int AvailableCpuThreads,
    [property: JsonPropertyName("available_gpu_slots")] int AvailableGpuSlots,
    [property: JsonPropertyName("gpu_utilization")] double GpuUtilization
);

/// <summary>
/// Background service that self-registers this process as a remote worker
/// with a coordinator, keeps the registration fresh via periodic
/// heartbeats, and unregisters cleanly on shutdown.
///
/// Only runs when <see cref="EncoderOptions.CoordinatorUrl"/> is set.
/// Standalone servers and coordinators themselves skip this — a
/// coordinator registering itself as its own worker works but isn't the
/// default; the operator has to explicitly opt in by pointing
/// CoordinatorUrl at the same host.
///
/// Network failures don't crash the process; the service logs warnings
/// and retries on the next heartbeat interval. The coordinator's
/// registry evicts stale workers after 60s of silence, so a brief
/// outage self-heals when the worker comes back.
///
/// License-gated token rotation (Phase 4.9)
/// ─────────────────────────────────────────
/// On every heartbeat tick the service checks whether the cached
/// <see cref="ClusterToken"/> is within 60 s of expiry.  If so it calls
/// <see cref="ILicenseTokenClient.RequestAsync"/> to rotate.
///
/// Failure handling:
///   • 401 from token endpoint → retry with exponential backoff (1–60 s).
///   • 403 from token endpoint → entitlement revoked; service stops
///     re-registering, sets <see cref="ClusterTokenHolder.IsRevoked"/>.
///   • Free-tier (no signing key AND first 403) → service shuts down cleanly.
/// </summary>
public class WorkerSelfRegistrationService(
    IHardwareCapabilities capabilities,
    EncoderOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkerSelfRegistrationService> logger,
    ClusterTokenHolder? tokenHolder = null,
    ILicenseTokenClient? licenseTokenClient = null
) : BackgroundService
{
    // Token is considered "near expiry" when it has less than this left.
    private static readonly TimeSpan TokenRefreshLeadTime = TimeSpan.FromSeconds(60);

    // Backoff sequence for repeated 401s (doubles each step, capped at 60 s).
    private static readonly TimeSpan[] BackoffSequence =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(32),
        TimeSpan.FromSeconds(60),
    ];

    // Tracks consecutive 401 failures for backoff index.
    private int _consecutiveAuthFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldRun())
        {
            logger.LogInformation(
                "Worker self-registration disabled (CoordinatorUrl and WorkerSelfBaseUrl both required)"
            );
            return;
        }

        HttpClient http = httpClientFactory.CreateClient("worker-self-registration");
        http.BaseAddress = new(options.CoordinatorUrl!);
        http.Timeout = TimeSpan.FromSeconds(10);

        // Perform initial token fetch before registering so heartbeats
        // carry the live cluster secret from the first request onward.
        await TryRefreshTokenAsync(stoppingToken).ConfigureAwait(false);

        if (!await TryRegisterAsync(http, stoppingToken).ConfigureAwait(false))
        {
            logger.LogWarning(
                "Initial registration with {CoordinatorUrl} failed — will retry on heartbeat loop",
                options.CoordinatorUrl
            );
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // Check for license revocation before sleeping — avoids one extra
            // heartbeat after a 403 is observed during the initial token fetch.
            if (tokenHolder?.IsRevoked == true)
            {
                logger.LogInformation("License revoked — worker self-registration stopped");
                return;
            }

            try
            {
                await Task.Delay(options.WorkerHeartbeatInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Rotate cluster token if it is near expiry.
            await MaybeRefreshTokenAsync(stoppingToken).ConfigureAwait(false);

            // Stop after revocation that was detected during token refresh.
            if (tokenHolder?.IsRevoked == true)
            {
                logger.LogInformation("License revoked — worker self-registration stopped");
                return;
            }

            bool alive = await TryHeartbeatAsync(http, stoppingToken).ConfigureAwait(false);
            if (alive)
                continue;
            // Heartbeat bounced — coordinator doesn't know us. Could be
            // coordinator restart or our eviction after a long outage.
            // Re-register rather than spamming a 404 heartbeat loop.
            logger.LogInformation("Heartbeat rejected by coordinator — attempting re-registration");
            await TryRegisterAsync(http, stoppingToken).ConfigureAwait(false);
        }

        await TryUnregisterAsync(http).ConfigureAwait(false);
    }

    private bool ShouldRun() =>
        !string.IsNullOrWhiteSpace(options.CoordinatorUrl)
        && !string.IsNullOrWhiteSpace(options.WorkerSelfBaseUrl)
        && options.IsDistributedEncodingEnabled;

    // ── Token rotation ────────────────────────────────────────────────────────

    private async Task TryRefreshTokenAsync(CancellationToken ct)
    {
        if (licenseTokenClient is null || tokenHolder is null)
            return;

        ClusterTokenResult result = await licenseTokenClient.RequestAsync(ct).ConfigureAwait(false);

        if (result.Token is not null)
        {
            tokenHolder.Update(result.Token);
            _consecutiveAuthFailures = 0;
            logger.LogDebug(
                "Cluster token refreshed, expires {ExpiresAt:O}",
                result.Token.ExpiresAt
            );
            return;
        }

        await HandleTokenFailureAsync(result, ct).ConfigureAwait(false);
    }

    private async Task MaybeRefreshTokenAsync(CancellationToken ct)
    {
        if (licenseTokenClient is null || tokenHolder is null)
            return;

        ClusterToken? current = tokenHolder.Current;
        bool nearExpiry =
            current is null || current.ExpiresAt - DateTime.UtcNow < TokenRefreshLeadTime;

        if (!nearExpiry)
            return;

        await TryRefreshTokenAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleTokenFailureAsync(ClusterTokenResult result, CancellationToken ct)
    {
        if (tokenHolder is null)
            return;

        switch (result.Failure)
        {
            case LicenseFailureKind.EntitlementRevoked:
                // Free-tier: no signing key was ever set AND we got 403 → just
                // shut down quietly without marking "revoked" in UI.
                if (!options.IsDistributedEncodingEnabled)
                {
                    logger.LogInformation(
                        "Free-tier install received 403 from token endpoint — distributed encoding not available"
                    );
                }
                else
                {
                    logger.LogWarning(
                        "Distributed encoding license revoked (403 from coordinator) — worker will stop re-registering. Resolve the license issue and restart."
                    );
                    tokenHolder.IsRevoked = true;
                }

                return;

            case LicenseFailureKind.Unauthenticated:
                _consecutiveAuthFailures++;
                int backoffIndex = Math.Min(
                    _consecutiveAuthFailures - 1,
                    BackoffSequence.Length - 1
                );
                TimeSpan delay = BackoffSequence[backoffIndex];
                logger.LogWarning(
                    "Token request returned 401 (failure #{Count}) — backing off {Delay} before next attempt",
                    _consecutiveAuthFailures,
                    delay
                );
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Service is stopping — let it fall through.
                }

                return;

            default:
                logger.LogDebug(
                    "Token request failed ({Failure}): {Message}",
                    result.Failure,
                    result.Message
                );
                return;
        }
    }

    // ── Registration / heartbeat / unregister ─────────────────────────────────

    private async Task<bool> TryRegisterAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            RegisterPayload payload = new(
                WorkerId: options.WorkerId,
                BaseUrl: options.WorkerSelfBaseUrl,
                CpuCores: capabilities.CpuCores,
                AvailableCpuThreads: capabilities.CpuCores,
                AvailableGpuSlots: capabilities.Gpus.Sum(g => g.MaxEncoderSessions),
                Gpus: capabilities.Gpus
            );

            using HttpResponseMessage response = await http.PostAsJsonAsync(
                    "api/v1/distribution/workers/register",
                    payload,
                    ct
                )
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Registered as worker {WorkerId} at {CoordinatorUrl}",
                    options.WorkerId,
                    options.CoordinatorUrl
                );
                return true;
            }

            logger.LogWarning(
                "Coordinator returned {StatusCode} on register — distribution disabled on coordinator side?",
                (int)response.StatusCode
            );
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The 10s HttpClient timeout above surfaces as a TaskCanceledException,
            // which IS an OperationCanceledException. Only the service's own
            // shutdown token may escape this catch — a slow coordinator must not,
            // or it ends the ExecuteAsync loop and stops the host.
            logger.LogWarning(ex, "Self-registration request failed");
            return false;
        }
    }

    private async Task<bool> TryHeartbeatAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            HeartbeatPayload payload = new(
                AvailableCpuThreads: capabilities.CpuCores,
                AvailableGpuSlots: capabilities.Gpus.Sum(g => g.MaxEncoderSessions),
                GpuUtilization: 0.0
            );

            using HttpResponseMessage response = await http.PostAsJsonAsync(
                    $"api/v1/distribution/workers/{options.WorkerId}/heartbeat",
                    payload,
                    ct
                )
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Same reasoning as TryRegisterAsync above: a heartbeat timeout must
            // not escape as an uncaught OperationCanceledException, or it stops
            // the host over one slow coordinator response.
            logger.LogDebug(ex, "Heartbeat failed");
            return false;
        }
    }

    private async Task TryUnregisterAsync(HttpClient http)
    {
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            await http.DeleteAsync($"api/v1/distribution/workers/{options.WorkerId}", cts.Token)
                .ConfigureAwait(false);
            logger.LogInformation("Unregistered worker {WorkerId} on shutdown", options.WorkerId);
        }
        catch (Exception ex)
        {
            // Shutdown path — log and move on. Coordinator's stale eviction
            // will clean us up within 60s if we can't confirm.
            logger.LogDebug(ex, "Unregister on shutdown failed (coordinator will evict stale)");
        }
    }
}
