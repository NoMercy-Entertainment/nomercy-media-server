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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Jobs;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// Manages the registry of remote encoder workers. Remote workers POST to
/// /register on boot, send periodic heartbeats to stay active, and either
/// unregister cleanly on shutdown or drop off after 60s of silence.
///
/// Endpoints gated by EncoderOptions.DistributedEncodingSigningKey being
/// set — single-machine installs return 503 and the registry stays empty.
/// </summary>
[ApiController]
[Tags("Distribution Workers")]
[ApiVersion(1.0)]
[Authorize]
// Primary route per the encoder spec.
[Route("api/v{version:apiVersion}/distribution/workers")]
// Legacy alias — kept for backwards compatibility with self-hosted users on
// older builds. Drop after a deprecation window.
[Obsolete("Use /api/v{version}/distribution/workers — kept for backwards compatibility")]
[Route("api/v{version:apiVersion}/dashboard/workers")]
public class WorkersController(
    InMemoryRemoteWorkerRegistry registry,
    ITaskSerializer serializer,
    ITaskProgressStore progressStore,
    EncoderOptions encoderOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<WorkersController> logger,
    ILogger<HttpRemoteWorker> workerLogger
) : BaseController
{
    [HttpGet]
    [Authorize(Policy = "Owner")]
    public IActionResult List()
    {
        // Full health snapshot (includes cooled-down workers) so operators
        // can see which workers are benched and why. The dispatcher uses a
        // narrower GetActiveWorkers() that hides cooldowns, but the dashboard
        // shows everyone with their current status.
        IReadOnlyList<WorkerHealthSnapshot> snapshots = registry.GetAllWorkersWithHealth();

        return Ok(
            new
            {
                distribution_enabled = encoderOptions.IsDistributedEncodingEnabled,
                count = snapshots.Count,
                active_count = snapshots.Count(s => s.CooldownUntilUtc is null),
                data = snapshots
                    .Select(s =>
                    {
                        ResourceBudgetSnapshot budget = s.Worker.GetAvailableBudget();
                        IHardwareCapabilities caps = s.Worker.GetCapabilities();
                        return new
                        {
                            worker_id = s.Worker.WorkerId,
                            available_gpu_slots = budget.AvailableGpuSlots,
                            available_cpu_threads = budget.AvailableCpuThreads,
                            gpu_utilization = budget.GpuUtilization,
                            cpu_cores = caps.CpuCores,
                            gpu_count = caps.Gpus.Count,
                            last_seen_utc = s.LastSeenUtc,
                            consecutive_failures = s.ConsecutiveFailures,
                            cooldown_until_utc = s.CooldownUntilUtc,
                            status = s.CooldownUntilUtc is null ? "active" : "cooldown",
                        };
                    })
                    .ToArray(),
            }
        );
    }

    [HttpPost("register")]
    public IActionResult Register([FromBody] RegisterWorkerRequest request)
    {
        if (!encoderOptions.IsDistributedEncodingEnabled)
            return ServiceUnavailableResponse(
                "Distributed encoding is not enabled on this server. "
                    + "Set DistributedEncodingSigningKey in EncoderOptions and restart."
            );

        if (!AuthPolicy.IsOwner(User))
            return UnauthorizedResponse("Only the server owner can register workers");

        if (string.IsNullOrWhiteSpace(request.WorkerId))
            return BadRequestResponse("worker_id is required");

        if (
            string.IsNullOrWhiteSpace(request.BaseUrl)
            || !Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out Uri? baseUri)
        )
            return BadRequestResponse("base_url must be an absolute URL");

        // Only HTTPS in production paths; local dev can still use HTTP via
        // loopback / localhost, but anything else must be TLS.
        bool isLoopback = baseUri.IsLoopback;
        if (!isLoopback && baseUri.Scheme != Uri.UriSchemeHttps)
            return BadRequestResponse(
                "base_url must use https:// (plain http is allowed only on loopback)"
            );

        HttpClient httpClient = httpClientFactory.CreateClient("remote-worker");
        httpClient.BaseAddress = baseUri;
        httpClient.Timeout = TimeSpan.FromMinutes(10); // Task encodes take minutes.

        HttpRemoteWorker worker = new(
            workerId: request.WorkerId,
            http: httpClient,
            serializer: serializer,
            signingKey: encoderOptions.GetDistributedEncodingSigningKey(),
            initialCapabilities: new HardwareCapabilities(
                Gpus: request.Gpus ?? [],
                CpuCores: request.CpuCores
            ),
            initialBudget: new(
                AvailableGpuSlots: request.AvailableGpuSlots,
                AvailableCpuThreads: request.AvailableCpuThreads,
                GpuUtilization: 0
            ),
            logger: workerLogger
        );

        registry.Register(worker);
        logger.LogInformation(
            "Registered remote worker {WorkerId} at {BaseUrl}",
            request.WorkerId,
            baseUri
        );

        return Ok(new { worker_id = request.WorkerId, registered = true });
    }

    [HttpPost("{workerId}/heartbeat")]
    public IActionResult Heartbeat(string workerId, [FromBody] HeartbeatRequest? request)
    {
        if (!encoderOptions.IsDistributedEncodingEnabled)
            return ServiceUnavailableResponse(
                "Distributed encoding is not enabled on this server."
            );

        if (!AuthPolicy.IsOwner(User))
            return UnauthorizedResponse("Only the server owner can send heartbeats");

        bool accepted = registry.Heartbeat(workerId);
        if (!accepted)
            return NotFoundResponse($"Worker '{workerId}' is not registered; re-register first");

        // If the heartbeat carries a fresh budget, push it into the worker
        // so the dispatcher sees current values. Capabilities rarely change
        // mid-run so we keep the original set.
        if (request is { AvailableCpuThreads: int cpu, AvailableGpuSlots: int gpu })
        {
            IRemoteWorker? existing = registry
                .GetActiveWorkers()
                .FirstOrDefault(w => w.WorkerId == workerId);
            if (existing is HttpRemoteWorker http)
            {
                http.UpdateSnapshot(
                    http.GetCapabilities(),
                    new(
                        AvailableGpuSlots: gpu,
                        AvailableCpuThreads: cpu,
                        GpuUtilization: request.GpuUtilization ?? 0
                    )
                );
            }
        }

        return Ok(new { worker_id = workerId, accepted = true });
    }

    /// <summary>
    /// Receives per-task progress updates from remote workers. The worker's
    /// HttpTaskProgressSink POSTs these every ~2 seconds during an encode;
    /// we cache the latest snapshot per task in the in-memory progress store
    /// so the dashboard can render live progress without holding persistent
    /// connections to every worker.
    ///
    /// Open endpoint (no bearer) — workers are headless processes; they
    /// authenticate via knowing the coordinator URL + being in the trusted
    /// network. Distribution signing key doesn't guard this endpoint
    /// because progress payloads contain no secrets; the worst a hostile
    /// caller can do is spoof a fake progress bar.
    /// </summary>
    [HttpPost("{workerId}/tasks/{taskId}/progress")]
    [AllowAnonymous]
    public IActionResult ReceiveProgress(
        string workerId,
        string taskId,
        [FromBody] ProgressUpdateRequest update
    )
    {
        if (!encoderOptions.IsDistributedEncodingEnabled)
            return ServiceUnavailableResponse(
                "Distributed encoding is not enabled on this server."
            );

        progressStore.Update(
            taskId,
            new(
                TaskId: taskId,
                WorkerId: workerId,
                PercentComplete: update.PercentComplete,
                CurrentFps: update.CurrentFps,
                CurrentSpeed: update.CurrentSpeed,
                CurrentStage: update.CurrentStage,
                ElapsedSeconds: update.ElapsedSeconds,
                EstimatedRemainingSeconds: update.EstimatedRemainingSeconds,
                CurrentTimeSeconds: update.CurrentTimeSeconds,
                DurationSeconds: update.DurationSeconds,
                ReceivedAtUtc: DateTime.UtcNow
            )
        );

        return NoContent();
    }

    /// <summary>
    /// Dashboard-facing list of currently-running remote tasks with their
    /// latest progress snapshot. Empty when no remote tasks are running.
    /// </summary>
    [HttpGet("tasks/progress")]
    [Authorize(Policy = "Owner")]
    public IActionResult ListActiveTaskProgress()
    {
        IReadOnlyList<TaskProgressSnapshot> snapshots = progressStore.GetAll();

        return Ok(
            new
            {
                count = snapshots.Count,
                data = snapshots
                    .Select(s => new
                    {
                        task_id = s.TaskId,
                        worker_id = s.WorkerId,
                        percent_complete = s.PercentComplete,
                        current_fps = s.CurrentFps,
                        current_speed = s.CurrentSpeed,
                        current_stage = s.CurrentStage,
                        elapsed_seconds = s.ElapsedSeconds,
                        estimated_remaining_seconds = s.EstimatedRemainingSeconds,
                        current_time_seconds = s.CurrentTimeSeconds,
                        duration_seconds = s.DurationSeconds,
                        received_at_utc = s.ReceivedAtUtc,
                    })
                    .ToArray(),
            }
        );
    }

    [HttpDelete("{workerId}")]
    [Authorize(Policy = "Owner")]
    public IActionResult Unregister(string workerId)
    {
        bool removed = registry.Unregister(workerId);
        if (!removed)
            return NotFoundResponse($"Worker '{workerId}' is not registered");

        logger.LogInformation("Unregistered remote worker {WorkerId}", workerId);
        return NoContent();
    }
}
