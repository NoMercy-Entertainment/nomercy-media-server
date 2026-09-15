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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.Status;

namespace NoMercy.Networking.Connectivity;

public class ConnectivityManager : IConnectivityManager, IHostedService, IDisposable
{
    private readonly INetworkDiscovery _networkDiscovery;
    private readonly IEnumerable<IConnectivityStrategy> _strategies;
    private readonly CancellationTokenSource _stoppingCts = new();
    private Task? _executingTask;
    private IConnectivityStrategy? _activeStrategy;

    public ConnectivityState CurrentState { get; private set; } = ConnectivityState.Starting;
    public ConnectivityType ActiveStrategy => _activeStrategy?.Type ?? ConnectivityType.LocalOnly;
    public event Action<ConnectivityState>? StateChanged;

    private readonly IAuthTokenStore _authTokenStore;
    private readonly IBootStatus _bootStatus;
    private readonly IConnectivityStatus _connectivityStatus;
    private readonly Func<Task>? _tunnelAvailability;
    private readonly Func<Task>? _transportChanged;

    // Collapses every supervision wait to a single short value. Set only by tests, which
    // otherwise could not exercise the recovery path without sleeping through the real
    // 30s-to-5min backoff.
    private readonly TimeSpan? _delayOverride;

    // A strategy that says "not ready" is deferred, not misjudged. This bounds how long that
    // deferral lasts before the strategy is attempted anyway and its real result is counted as
    // evidence — a wrong precondition (or one that never resolves) can never permanently hide
    // a transport. 90s comfortably covers a cloudflared download that has not landed yet
    // without leaving a broken precondition silent forever.
    private static readonly TimeSpan DefaultReadinessPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultReadinessDeferralWindow = TimeSpan.FromSeconds(90);
    private readonly TimeSpan _readinessDeferralWindow;

    private readonly ILogger<ConnectivityManager> _logger;

    public ConnectivityManager(
        ILogger<ConnectivityManager> logger,
        IAuthTokenStore authTokenStore,
        INetworkDiscovery networkDiscovery,
        IEnumerable<IConnectivityStrategy> strategies,
        IBootStatus bootStatus,
        IConnectivityStatus connectivityStatus,
        Func<Task>? tunnelAvailability = null,
        TimeSpan? delayOverride = null,
        TimeSpan? readinessDeferralWindow = null,
        IHostApplicationLifetime? lifetime = null,
        Func<Task>? transportChanged = null
    )
    {
        _transportChanged = transportChanged;
        _logger = logger;
        _authTokenStore = authTokenStore;
        _networkDiscovery = networkDiscovery;
        _strategies = strategies.OrderBy(s => s.Priority);
        _bootStatus = bootStatus;
        _connectivityStatus = connectivityStatus;
        _tunnelAvailability = tunnelAvailability;
        _delayOverride = delayOverride;
        _readinessDeferralWindow = readinessDeferralWindow ?? DefaultReadinessDeferralWindow;

        // Fires before any IHostedService.StopAsync — including Kestrel's — so the active
        // strategy learns shutdown has begun while the origin is still reachable, not after
        // it has already gone dark and logged a burst of "connection refused".
        lifetime?.ApplicationStopping.Register(() => _activeStrategy?.BeginShutdown());
    }

    /// <summary>
    /// Attempt order for the current evaluation: plain priority, direct paths first. A
    /// direct path only wins when it is verified from outside, and an assigned tunnel that
    /// registers with the edge still beats a port forward nothing could confirm, because an
    /// unverified result is only ever held as a fallback. Putting the tunnel first whenever
    /// a token existed sent every client through Cloudflare even when the router forwarded
    /// perfectly well.
    /// </summary>
    private IEnumerable<IConnectivityStrategy> OrderedStrategies() => _strategies;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _executingTask = ExecuteAsync(_stoppingCts.Token);
        return _executingTask.IsCompleted ? _executingTask : Task.CompletedTask;
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_bootStatus.IsStarted && !cancellationToken.IsCancellationRequested)
                await Task.Delay(1000, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await WaitForAuthenticationAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            // Discover external IP + UPnP BEFORE evaluating strategies,
            // so IsPortOpenAsync has the real external IP (not "0.0.0.0")
            await _networkDiscovery.DiscoverExternalIpAsync();

            await EvaluateAsync(cancellationToken);
            await SuperviseAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error in ConnectivityManager: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Waits for an access token for as long as it takes — an operator slower than 30s to
    /// sign in must still get connectivity evaluated once they do, not leave the server
    /// local-only for the rest of the run. Only cancellation (shutdown) breaks the wait.
    /// </summary>
    private async Task WaitForAuthenticationAsync(CancellationToken cancellationToken)
    {
        if (_authTokenStore.AccessToken is not null)
            return;

        const int initialWarnAfterSeconds = 30;

        _logger.LogDebug("ConnectivityManager waiting for authentication...");
        int waitedSeconds = 0;
        bool warned = false;

        while (_authTokenStore.AccessToken is null && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);
            waitedSeconds++;

            if (!warned && waitedSeconds >= initialWarnAfterSeconds)
            {
                warned = true;
                // A server that boots before auth settles must not go unreachable in
                // silence. This used to give up permanently here, so a server whose
                // operator took longer than 30s to sign in stayed local-only until
                // somebody restarted it by hand.
                _logger.LogWarning(
                    "ConnectivityManager still has no authentication after {Seconds}s — "
                        + "connectivity stays local-only until sign-in completes, still waiting",
                    initialWarnAfterSeconds
                );
            }
        }
    }

    public async Task EvaluateAsync(CancellationToken ct)
    {
        SetState(ConnectivityState.Evaluating);

        // Tear down any existing strategy
        if (_activeStrategy is not null)
        {
            await _activeStrategy.TeardownAsync();
            _activeStrategy = null;
        }

        ConnectivityMode mode = RuntimeServerSettings.Current.ConnectivityMode;
        if (mode is not ConnectivityMode.Auto)
            _logger.LogInformation("Connectivity mode is pinned to {Mode}", mode);

        if (mode is ConnectivityMode.LocalOnly)
        {
            SetState(ConnectivityState.LocalOnly);
            await ReportTransportAsync("local");
            return;
        }

        // Ask whether a tunnel has been provisioned BEFORE ranking anything. This used to
        // happen inside the tunnel strategy, which sits last, so a server that never got
        // that far never learned a tunnel had been assigned to it at all.
        await RefreshTunnelAvailabilityAsync();

        IConnectivityStrategy? unverified = null;

        foreach (IConnectivityStrategy strategy in OrderedStrategies())
        {
            if (ct.IsCancellationRequested)
                break;

            if (!IsAllowedByMode(strategy, mode))
            {
                _logger.LogDebug(
                    "Skipping {Name} — connectivity mode is pinned to {Mode}",
                    [strategy.Name, mode]
                );
                continue;
            }

            await WaitUntilReadyAsync(strategy, ct);

            try
            {
                _logger.LogInformation("Trying connectivity strategy: {Name}", strategy.Name);
                ConnectivityResult result = await strategy.TryEstablishAsync(ct);

                if (result is { Established: true, Confidence: ConnectivityConfidence.Verified })
                {
                    await ActivateAsync(strategy);
                    return;
                }

                if (result.Established)
                {
                    // It reported success but could not prove it. Hold it as a fallback and
                    // keep looking: an assigned tunnel that verifies itself is a better
                    // answer than a port mapping nothing could confirm. Tear it down in the
                    // meantime so no transport is left half-running.
                    _logger.LogInformation(
                        "Strategy {Name} succeeded but could not be verified — holding it as a fallback",
                        strategy.Name
                    );
                    await strategy.TeardownAsync();
                    unverified ??= strategy;
                    continue;
                }

                _logger.LogDebug("Strategy {Name} did not succeed, trying next...", strategy.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Strategy {Name} failed: {Message}",
                    [strategy.Name, ex.Message]
                );
            }
        }

        if (unverified is not null && !ct.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Nothing could prove remote reachability — falling back to {Name}",
                unverified.Name
            );

            // The first Failed() this returns must not be read as success. Discarding it and
            // activating unconditionally is exactly how DirectAccess got entered on a fallback
            // that had just failed its own re-establish.
            ConnectivityResult fallbackResult = await unverified.TryEstablishAsync(ct);
            if (fallbackResult.Established)
            {
                await ActivateAsync(unverified);
                return;
            }

            _logger.LogWarning(
                "Fallback strategy {Name} could not be re-established on retry",
                unverified.Name
            );
        }

        SetState(ConnectivityState.LocalOnly);
        _logger.LogWarning("No remote connectivity strategy succeeded — server is local-only");
        await ReportTransportAsync("local");
    }

    /// <summary>
    /// Gives a strategy whose preconditions are not met a bounded window to become ready
    /// before it is judged at all. Skipping straight to TryEstablishAsync while a strategy
    /// reports IsReady false is what turned "cloudflared has not finished downloading" into
    /// "the tunnel does not work" — the same Failed() shape for two unrelated situations. Once
    /// the window elapses the strategy is attempted anyway so a precondition that never
    /// resolves (or was wrong) cannot hide a transport forever.
    /// </summary>
    private async Task WaitUntilReadyAsync(IConnectivityStrategy strategy, CancellationToken ct)
    {
        if (strategy.IsReady)
            return;

        TimeSpan pollInterval = _delayOverride ?? DefaultReadinessPollInterval;
        DateTime deadline = DateTime.UtcNow + _readinessDeferralWindow;

        _logger.LogInformation(
            "{Name} is not ready yet — deferring for up to {Seconds}s rather than counting this as a failed attempt",
            [strategy.Name, _readinessDeferralWindow.TotalSeconds]
        );

        while (!strategy.IsReady && DateTime.UtcNow < deadline)
            await Task.Delay(pollInterval, ct);

        if (!strategy.IsReady)
            _logger.LogWarning(
                "{Name} was still not ready after {Seconds}s — attempting anyway so a wrong precondition can't hide it forever",
                [strategy.Name, _readinessDeferralWindow.TotalSeconds]
            );
    }

    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    private static readonly TimeSpan SupervisionInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Keeps trying after the first evaluation, and notices when an established transport
    /// dies. Evaluating exactly once per process made every transient cause permanent: the
    /// most common one is that cloudflared is still downloading when the first evaluation
    /// runs, so the tunnel fails to start and a server with no other viable transport stays
    /// local-only until somebody restarts it by hand.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            if (CurrentState is ConnectivityState.LocalOnly)
            {
                // LocalOnly means two different things and only one of them is a retry
                // candidate: the operator pinning the mode is a real terminal choice, while
                // "nothing worked this pass" is not. Backing off and logging "retrying" for a
                // pinned choice implied the server was still hunting for connectivity when it
                // had deliberately stopped looking.
                bool pinned =
                    RuntimeServerSettings.Current.ConnectivityMode is ConnectivityMode.LocalOnly;

                TimeSpan delay = pinned
                    ? _delayOverride ?? SupervisionInterval
                    : _delayOverride ?? RetryBackoff[Math.Min(attempt, RetryBackoff.Length - 1)];

                if (pinned)
                {
                    attempt = 0;
                }
                else
                {
                    attempt++;
                    _logger.LogInformation(
                        "No remote connectivity — retrying in {Seconds}s",
                        delay.TotalSeconds
                    );
                }

                await Task.Delay(delay, ct);
                await EvaluateAsync(ct);
                continue;
            }

            if (_activeStrategy is { IsStillEstablished: false })
            {
                _logger.LogWarning(
                    "Connectivity via {Name} dropped — re-evaluating",
                    _activeStrategy.Name
                );
                attempt = 0;
                await EvaluateAsync(ct);
                continue;
            }

            attempt = 0;
            await Task.Delay(_delayOverride ?? SupervisionInterval, ct);
        }
    }

    private async Task RefreshTunnelAvailabilityAsync()
    {
        if (_tunnelAvailability is null)
            return;

        try
        {
            await _tunnelAvailability();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Tunnel availability check failed: {Message}", ex.Message);
        }
    }

    private static bool IsAllowedByMode(IConnectivityStrategy strategy, ConnectivityMode mode)
    {
        return mode switch
        {
            ConnectivityMode.PortForward => strategy.Type is ConnectivityType.PortForward,
            ConnectivityMode.CloudflareTunnel => strategy.Type is ConnectivityType.CloudflareTunnel,
            ConnectivityMode.LocalOnly => false,
            _ => true,
        };
    }

    private async Task ReportTransportAsync(string transport)
    {
        _connectivityStatus.Transport = transport;

        if (_transportChanged is null)
            return;

        try
        {
            await _transportChanged();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not report the transport: {Message}", ex.Message);
        }
    }

    private static string TransportName(ConnectivityType type) =>
        type switch
        {
            ConnectivityType.PortForward => "port_forward",
            ConnectivityType.CloudflareTunnel => "tunnel",
            _ => "local",
        };

    private async Task ActivateAsync(IConnectivityStrategy strategy)
    {
        Activate(strategy);
        await ReportTransportAsync(TransportName(strategy.Type));
    }

    private void Activate(IConnectivityStrategy strategy)
    {
        _activeStrategy = strategy;
        SetState(
            strategy.Type switch
            {
                ConnectivityType.PortForward => ConnectivityState.DirectAccess,
                ConnectivityType.StunHolePunch => ConnectivityState.HolePunched,
                ConnectivityType.CloudflareTunnel => ConnectivityState.Tunneled,
                _ => ConnectivityState.DirectAccess,
            }
        );
        _logger.LogInformation("Connectivity established via {Name}", strategy.Name);
    }

    private void SetState(ConnectivityState state)
    {
        if (CurrentState == state)
            return;
        CurrentState = state;
        StateChanged?.Invoke(state);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_executingTask == null)
            return;

        try
        {
            await _stoppingCts.CancelAsync();
        }
        finally
        {
            await Task.WhenAny(
                _executingTask,
                Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)
            );
        }

        if (_activeStrategy is not null)
        {
            await _activeStrategy.TeardownAsync();
        }

        // A UPnP mapping is created with an unbounded lifetime, so it outlives this
        // process on the router unless explicitly removed — a stale forward left behind
        // on every clean stop/restart.
        await _networkDiscovery.RemovePortMappingsAsync();
    }

    public void Dispose()
    {
        try
        {
            if (!_stoppingCts.IsCancellationRequested)
                _stoppingCts.Cancel();
        }
        catch (ObjectDisposedException) { }

        try
        {
            _stoppingCts.Dispose();
        }
        catch (ObjectDisposedException) { }

        GC.SuppressFinalize(this);
    }
}
