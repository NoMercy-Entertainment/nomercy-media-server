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

using System.IdentityModel.Tokens.Jwt;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using Serilog.Events;

namespace NoMercy.Setup.Boot;

public interface IDegradedModeRecovery
{
    Task StartRecoveryLoop(DeferredTasks tasks, CancellationToken ct = default);

    /// <summary>
    /// Per-component snapshot of the recovery loop's current progress, read by
    /// <c>/health/detailed</c> so a caller can distinguish "auth still pending" from
    /// "network still down" from "not registered yet" instead of one flat degraded flag.
    /// </summary>
    RecoveryStatus CurrentStatus { get; }
}

/// <summary>
/// Snapshot of <see cref="DegradedModeRecovery.StartRecoveryLoop"/>'s progress, exposed
/// read-only for <c>/health/detailed</c>.
/// </summary>
public sealed record RecoveryStatus
{
    public bool IsRunning { get; init; }
    public bool ApiKeysLoaded { get; init; }
    public bool Authenticated { get; init; }
    public bool NetworkDiscovered { get; init; }
    public bool Registered { get; init; }
    public bool BinariesReady { get; init; }
}

public class DegradedModeRecovery : IDegradedModeRecovery
{
    private readonly IApiKeyLoader _apiKeyLoader;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IServerRegistrationService _serverRegistrationService;
    private readonly INetworkDiscovery? _networkDiscovery;

    private readonly IAuthTokenStore _authTokenStore;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<Task<bool>> _checkConnectivity;
    private readonly Func<bool>? _binaryExists;
    private readonly Func<Task>? _downloadBinary;
    private readonly IEventBus? _eventBus;

    public DegradedModeRecovery(
        IAuthTokenStore authTokenStore,
        IApiKeyLoader apiKeyLoader,
        IApiKeyStore apiKeyStore,
        IServerRegistrationService serverRegistrationService,
        INetworkDiscovery? networkDiscovery = null
    )
        : this(
            authTokenStore,
            apiKeyLoader,
            apiKeyStore,
            serverRegistrationService,
            networkDiscovery,
            delay: null
        ) { }

    /// <summary>
    /// Internal (not exposed on the public constructor): lets NoMercy.Tests.Setup drive
    /// <see cref="StartRecoveryLoop"/> through multiple backoff ticks without the real
    /// wall-clock <see cref="BackoffSchedule"/> (30s-30m) — the only DI-visible constructor
    /// remains the public one above, so production callers and the built-in
    /// ServiceProvider are unaffected.
    /// </summary>
    internal DegradedModeRecovery(
        IAuthTokenStore authTokenStore,
        IApiKeyLoader apiKeyLoader,
        IApiKeyStore apiKeyStore,
        IServerRegistrationService serverRegistrationService,
        INetworkDiscovery? networkDiscovery,
        Func<TimeSpan, Task>? delay,
        IEventBus? eventBus = null,
        Func<Task<bool>>? checkConnectivity = null,
        Func<bool>? binaryExists = null,
        Func<Task>? downloadBinary = null
    )
    {
        _authTokenStore = authTokenStore;
        _apiKeyLoader = apiKeyLoader;
        _apiKeyStore = apiKeyStore;
        _serverRegistrationService = serverRegistrationService;
        _networkDiscovery = networkDiscovery;
        _delay = delay ?? Task.Delay;
        _checkConnectivity = checkConnectivity ?? (() => NetworkProbe.CheckConnectivity());
        _binaryExists = binaryExists;
        _downloadBinary = downloadBinary;
        _eventBus = eventBus;
    }

    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    private volatile RecoveryStatus _currentStatus = new();

    public RecoveryStatus CurrentStatus => _currentStatus;

    public async Task StartRecoveryLoop(DeferredTasks tasks, CancellationToken ct = default)
    {
        int attempt = 0;
        _currentStatus = BuildStatus(tasks, isRunning: true);

        try
        {
            while (!tasks.AllCompleted && !ct.IsCancellationRequested)
            {
                TimeSpan delay = BackoffSchedule[Math.Min(attempt, BackoffSchedule.Length - 1)];

                // Race the injectable delay against the shutdown token instead of changing
                // _delay's signature — the real production delay (Task.Delay) has no way to
                // observe cancellation mid-sleep on its own, and every existing test injects
                // a single-argument no-op delegate that a token-aware signature would break.
                Task cancellation = Task.Delay(Timeout.Infinite, ct);
                Task completed = await Task.WhenAny(_delay(delay), cancellation);
                if (completed == cancellation)
                    break;

                bool hasNetwork = await _checkConnectivity();
                if (!hasNetwork)
                {
                    attempt++;
                    if (!tasks.BinariesReady)
                    {
                        string reason = "Network unavailable; playback tools cannot be downloaded";
                        Logger.App(
                            $"Playback tools download failed: {reason}",
                            LogEventLevel.Error
                        );
                        IEventBus? bus =
                            _eventBus
                            ?? (EventBusProvider.IsConfigured ? EventBusProvider.Current : null);
                        if (bus is not null)
                            await bus.PublishAsync(
                                new PlaybackToolsDownloadFailedEvent
                                {
                                    ErrorMessage = reason,
                                    Attempt = attempt + 1,
                                    NextRetryAtUtc = DateTimeOffset.UtcNow.Add(
                                        BackoffSchedule[
                                            Math.Min(attempt, BackoffSchedule.Length - 1)
                                        ]
                                    ),
                                },
                                ct
                            );
                    }
                    Logger.App(
                        $"Network still unavailable. Next retry in {BackoffSchedule[Math.Min(attempt, BackoffSchedule.Length - 1)]}"
                    );
                    _currentStatus = BuildStatus(tasks, isRunning: true);
                    continue;
                }

                Logger.App("Network connectivity restored — executing deferred tasks");

                if (!tasks.BinariesReady)
                {
                    await TryProvisionBinariesAsync(
                        tasks,
                        attempt: attempt + 2,
                        nextRetryAtUtc: DateTimeOffset.UtcNow.Add(
                            BackoffSchedule[Math.Min(attempt + 1, BackoffSchedule.Length - 1)]
                        ),
                        eventBus: _eventBus,
                        binaryExists: _binaryExists,
                        download: _downloadBinary ?? DownloadAllBinariesAsync
                    );
                }

                if (!tasks.ApiKeysLoaded)
                {
                    try
                    {
                        await _apiKeyLoader.LoadKeys();
                        tasks.ApiKeysLoaded = _apiKeyStore.KeysLoaded;
                    }
                    catch (Exception e)
                    {
                        Logger.App($"Deferred ApiInfo failed: {e.Message}", LogEventLevel.Warning);
                    }
                }

                if (tasks is { Authenticated: false, ApiKeysLoaded: true })
                {
                    string? token = _authTokenStore.AccessToken;
                    if (string.IsNullOrEmpty(token))
                    {
                        // Auth not ready — AuthManager background refresh will handle it
                        Logger.App(
                            "Auth not ready — waiting for AuthManager background refresh",
                            LogEventLevel.Verbose
                        );
                    }
                    else
                    {
                        tasks.Authenticated = true;
                    }
                }

                if (!tasks.NetworkDiscovered)
                {
                    try
                    {
                        if (_networkDiscovery is not null)
                            await _networkDiscovery.DiscoverExternalIpAsync();
                        tasks.NetworkDiscovered = true;
                        ServerPhaseTracker.Current?.MarkComplete(BootStage.Network);
                    }
                    catch (Exception e)
                    {
                        Logger.App(
                            $"Deferred network discovery failed: {e.Message}",
                            LogEventLevel.Warning
                        );
                    }
                }

                if (tasks is { Registered: false, Authenticated: true, NetworkDiscovered: true })
                {
                    try
                    {
                        // Ensure token is present and not expired before attempting registration.
                        // AuthManager background refresh keeps the token alive; a null/empty check
                        // is not sufficient — nomercy-tv will reject an expired JWT.
                        bool tokenNeedsRefresh = true;

                        string? registrationToken = _authTokenStore.AccessToken;
                        if (!string.IsNullOrEmpty(registrationToken))
                        {
                            try
                            {
                                JwtSecurityTokenHandler tokenHandler = new();
                                JwtSecurityToken parsedToken = tokenHandler.ReadJwtToken(
                                    registrationToken
                                );
                                tokenNeedsRefresh =
                                    parsedToken.ValidTo <= DateTime.UtcNow.AddSeconds(30);
                            }
                            catch
                            {
                                // Token could not be parsed — treat as expired
                            }
                        }

                        if (tokenNeedsRefresh)
                        {
                            Logger.App(
                                "Access token missing or expired before deferred registration — waiting for AuthManager background refresh",
                                LogEventLevel.Warning
                            );
                            // Auth not ready — AuthManager background refresh will handle it
                            continue;
                        }

                        await _serverRegistrationService.Init();
                        tasks.Registered = true;
                        ServerPhaseTracker.Current?.MarkComplete(BootStage.Registered);
                    }
                    catch (InvalidOperationException e) when (e.Message.Contains("cooldown"))
                    {
                        // Cooldown active — will retry on next loop iteration
                        Logger.App(
                            $"Deferred registration deferred: {e.Message}",
                            LogEventLevel.Debug
                        );
                    }
                    catch (Exception e)
                    {
                        Logger.App(
                            $"Deferred registration failed: {e.Message}",
                            LogEventLevel.Warning
                        );
                    }
                }

                if (
                    tasks is
                    {
                        ApiKeysLoaded: true,
                        Authenticated: true,
                        NetworkDiscovered: true,
                        SeedsRun: true,
                        Registered: true,
                        BinariesReady: true
                    }
                )
                {
                    tasks.AllCompleted = true;
                    // Recovery finished — clear the flag HealthController.GetDetailed reads so
                    // /health/detailed goes back to "healthy" instead of staying degraded forever
                    // after this one recovery completes.
                    Start.IsDegradedMode = false;
                    Logger.App("Full mode restored — all deferred tasks completed");
                }

                _currentStatus = BuildStatus(tasks, isRunning: !tasks.AllCompleted);
                attempt++;
            }
        }
        catch (OperationCanceledException)
        {
            Logger.App("Degraded-mode recovery loop cancelled — server is shutting down");
        }
        finally
        {
            _currentStatus = BuildStatus(tasks, isRunning: false);
        }
    }

    private static RecoveryStatus BuildStatus(DeferredTasks tasks, bool isRunning)
    {
        return new()
        {
            IsRunning = isRunning,
            ApiKeysLoaded = tasks.ApiKeysLoaded,
            Authenticated = tasks.Authenticated,
            NetworkDiscovered = tasks.NetworkDiscovered,
            Registered = tasks.Registered,
            BinariesReady = tasks.BinariesReady,
        };
    }

    private static Task DownloadAllBinariesAsync()
    {
        IStorageDriver driver = new LocalStorageDriver();
        IStorage storage = new LocalStorage(driver, new([], driver));
        return new Binaries(driver, storage).DownloadAll();
    }

    /// <summary>
    /// Retries essential-binary provisioning (ffmpeg and friends) with the recovery
    /// loop's backoff schedule. A transient failure on first boot (GitHub rate limit,
    /// network blip, momentarily-empty release feed) must not permanently strand
    /// <c>BootStage.Binaries</c>. The recovery loop retries the full download sequence;
    /// each dependency method skips a binary that is already current.
    /// </summary>
    /// <remarks>Internal (not private) so <c>NoMercy.Tests.Setup</c> can exercise the
    /// ffmpeg-already-on-disk path directly instead of waiting through the loop's
    /// real backoff delays. The production loop supplies a download delegate so other
    /// failed dependencies are retried even when ffmpeg is present.</remarks>
    internal static async Task TryProvisionBinariesAsync(
        DeferredTasks tasks,
        int attempt = 2,
        DateTimeOffset? nextRetryAtUtc = null,
        IEventBus? eventBus = null,
        Func<bool>? binaryExists = null,
        Func<Task>? download = null
    )
    {
        if (tasks.BinariesReady)
            return;

        IEventBus? bus =
            eventBus ?? (EventBusProvider.IsConfigured ? EventBusProvider.Current : null);
        try
        {
            IStorageDriver driver = new LocalStorageDriver();
            IStorage storage = new LocalStorage(driver, new([], driver));
            Func<bool> exists = binaryExists ?? (() => storage.Exists(AppFiles.FfmpegPath));

            if (exists() && download is null)
            {
                tasks.BinariesReady = true;
                ServerPhaseTracker.Current?.MarkComplete(BootStage.Binaries);
                Logger.App("FFmpeg found on disk — Binaries boot stage marked complete");
                if (bus is not null)
                    await bus.PublishAsync(new PlaybackToolsReadyEvent { Attempt = attempt });
                return;
            }

            Logger.App("Retrying deferred binary provisioning", LogEventLevel.Warning);

            // DownloadAll retries incomplete dependencies after a deferred boot task.
            // Each download method skips a binary that is already current.
            if (download is not null)
                await download();
            else
                await new Binaries(driver, storage).DownloadAll();

            if (exists())
            {
                tasks.BinariesReady = true;
                ServerPhaseTracker.Current?.MarkComplete(BootStage.Binaries);
                Logger.App(
                    "Deferred binary provisioning succeeded — Binaries boot stage marked complete"
                );
                if (bus is not null)
                    await bus.PublishAsync(new PlaybackToolsReadyEvent { Attempt = attempt });
            }
            else
                throw new InvalidOperationException(
                    "FFmpeg is still missing after download completed"
                );
        }
        catch (Exception e)
        {
            Logger.App(
                $"Deferred binary provisioning failed: {e.Message} — will retry",
                LogEventLevel.Error
            );
            if (bus is not null)
                await bus.PublishAsync(
                    new PlaybackToolsDownloadFailedEvent
                    {
                        ErrorMessage = e.Message,
                        Attempt = attempt,
                        NextRetryAtUtc = nextRetryAtUtc ?? DateTimeOffset.UtcNow.AddMinutes(1),
                    }
                );
        }
    }
}
