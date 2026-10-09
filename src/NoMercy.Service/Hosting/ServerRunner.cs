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

using System.Diagnostics;
using System.Net.Sockets;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Status;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Setup.Ui;
using NoMercyQueue;

namespace NoMercy.Service.Hosting;

public class ServerRunner : IServerRunner
{
    private readonly ILogger<ServerRunner> _logger;
    private readonly IPortManager _portManager;
    private readonly IShutdownCoordinator _shutdownCoordinator;

    public ServerRunner(
        ILogger<ServerRunner> logger,
        IPortManager portManager,
        IShutdownCoordinator shutdownCoordinator
    )
    {
        _logger = logger;
        _portManager = portManager;
        _shutdownCoordinator = shutdownCoordinator;
    }

    public async Task<bool> RunWithHttpsRestart(
        WebApplication httpHost,
        StartupOptions options,
        BootOrchestrator orchestrator
    )
    {
        IShutdownCoordinator shutdownCoordinator =
            httpHost.Services.GetRequiredService<IShutdownCoordinator>();

        SetupState setupState = httpHost.Services.GetRequiredService<SetupState>();

        // Start the HTTP host
        try
        {
            await httpHost.StartAsync(shutdownCoordinator.Token);
        }
        catch (IOException ex)
            when (ex.InnerException is SocketException
                || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            )
        {
            bool shouldRetry = await _portManager.HandlePortInUse(
                RuntimeServerSettings.Current.InternalServerPort,
                ex
            );
            await httpHost.DisposeAsync();
            return shouldRetry;
        }

        SetupAddress setupAddress = SetupAddress.Current();
        string setupUrl = setupAddress.LocalhostUrl;
        _logger.LogInformation("Server is in setup mode. Please complete setup.");
        foreach (string line in setupAddress.Lines)
            _logger.LogInformation("{SetupAddressLine}", line);

        // Try to open the browser automatically if running interactively.
        if (!options.RunAsService && AuthManager.IsDesktopEnvironment())
        {
            try
            {
                AuthManager.OpenBrowser(setupUrl);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    "Could not open browser automatically: {Message}",
                    ex.Message
                );
                _logger.LogInformation(
                    "Please open your browser and navigate to: {SetupUrl}",
                    setupUrl
                );
            }
        }

        // Headless environments (Docker, NAS, systemd) cannot open a browser — start
        // the device code flow so the user can authenticate from another device.
        if (options.RunAsService || !AuthManager.IsDesktopEnvironment())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await orchestrator.StartHeadlessDeviceCodeFlowAsync(
                            shutdownCoordinator.Token
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Headless device code flow error: {Message}",
                            ex.Message
                        );
                    }
                },
                shutdownCoordinator.Token
            );
        }

        // Wait for either setup completion or shutdown.
        // Use the host's ApplicationStopping token so that POST /manage/stop
        // (which calls IHostApplicationLifetime.StopApplication()) also cancels
        // this wait — not just Ctrl+C via shutdownCoordinator.
        CancellationToken hostStopping = httpHost
            .Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownCoordinator.Token,
            hostStopping
        );

        // Run post-auth (registration + cert) in background — RunPostAuthAsync
        // waits for Authenticated itself, then runs Phase 3 and transitions to
        // Complete.
        //
        // It must be RunPostAuthAsync, not the bare RunRegistrationAsync: only the
        // former marks BootStage.Auth and starts the background tasks that mark
        // Binaries and Network. BootStage.All is Essential|Auth|Binaries|Network|
        // Registered, and the library/import queues refuse to run until All is
        // reached — so calling the inner method left a freshly onboarded server
        // with every library scan queued forever and ffmpeg never downloaded,
        // recoverable only by restarting.
        Task postAuthTask = Task.Run(
            async () =>
            {
                try
                {
                    await orchestrator.RunPostAuthAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested
                }
            },
            linkedCts.Token
        );

        // Wait for setup to reach a terminal outcome (Complete or Failed) or for
        // the server to be shut down.
        try
        {
            await setupState.WaitForTerminalPhaseAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            await httpHost.StopAsync(TimeSpan.FromSeconds(10));
            await httpHost.DisposeAsync();
            return false;
        }

        // Setup completed — a certificate should now be usable. EnsureHttpsCertificate()
        // accepts either the real Let's Encrypt cert or the self-signed fallback: when LE
        // issuance is slow/rate-limited/unreachable (LAN-only box), the self-signed cert
        // keeps the origin on HTTPS so an HTTPS-only dashboard can still reach it. Only
        // when self-signed generation itself fails do we stay on plaintext HTTP.
        if (!Start.Certificate!.EnsureHttpsCertificate())
        {
            _logger.LogInformation(
                "Setup completed but no usable certificate (including self-signed fallback) — continuing on HTTP"
            );
            await httpHost.WaitForShutdownAsync(shutdownCoordinator.Token);
            await httpHost.DisposeAsync();
            return false;
        }

        _logger.LogInformation("Certificate ready — restarting with HTTPS...");

        // Give the SSO callback page time to deliver its response to the browser
        await Task.Delay(3000);

        // Gracefully stop the HTTP host. Cancel the old coordinator while its token
        // source is still alive so background work bound to it winds down before the
        // container — and the CancellationTokenSource behind _shutdownCoordinator — is
        // disposed. Calling RequestShutdown() after DisposeAsync threw
        // ObjectDisposedException on every cert-acquired setup restart.
        httpHost.Services.GetRequiredService<IBootStatus>().MarkStopped();
        _shutdownCoordinator.RequestShutdown();

        // Stop this host's queue workers before its container goes away. They poll a
        // queue that outlives the host, so without this they keep reserving jobs and
        // scoping them against a provider that no longer exists — every one of those
        // jobs dies with ObjectDisposedException and is dead-lettered rather than
        // retried, because the worker was never told to stop and so reads the failure
        // as the job's fault. The HTTPS host starts its own workers on the same queue
        // moments later, so the two sets race and whichever the dead one wins is lost.
        // Measured on a first boot: 367 failed jobs, a library added after the restart
        // that never scanned, and 9 of 17 films imported.
        await httpHost.Services.GetRequiredService<QueueRunner>().StopAll();

        await httpHost.StopAsync(TimeSpan.FromSeconds(10));
        await httpHost.DisposeAsync();

        // Build and start a new host with HTTPS
        Stopwatch restartStopWatch = new();
        restartStopWatch.Start();

        WebApplication httpsHost = WebHostFactory.Create(options);

        IShutdownCoordinator httpsShutdownCoordinator =
            httpsHost.Services.GetRequiredService<IShutdownCoordinator>();
        IPortManager httpsPortManager = httpsHost.Services.GetRequiredService<IPortManager>();

        // The new DI container has a fresh AuthManager and SetupState — load tokens
        // from DB and mark setup complete so the middleware stops blocking requests.
        AuthManager httpsAuthManager = httpsHost.Services.GetRequiredService<AuthManager>();
        bool hasValidToken = await httpsAuthManager.InitializeAsync();

        // isRegistered must reflect whether a certificate actually exists — the
        // setup flow that just ran may have finished degraded (registered, cert
        // not acquired). Hardcoding true here made the fresh host believe setup
        // was fully done and dropped the /setup recovery UI even when it wasn't.
        // WebHostFactory.Create(options) above already rewired Start.Certificate
        // to this host's own ICertificateService (ServiceConfiguration.Core.cs),
        // so the static accessor reflects the fresh container, not the old one.
        SetupState httpsSetupState = httpsHost.Services.GetRequiredService<SetupState>();
        httpsSetupState.DetermineInitialPhase(
            hasValidToken: hasValidToken,
            isRegistered: Start.Certificate!.HasValidCertificate()
        );

        httpsAuthManager.ScheduleBackgroundRefresh(httpsShutdownCoordinator.Token);

        // Force the DI container to instantiate QueueRunner (it's a lazy singleton).
        // The constructor sets QueueRunner.Current = this.
        QueueRunner httpsQueueRunner = httpsHost.Services.GetRequiredService<QueueRunner>();

        // Initialize queue workers so they can process jobs on the HTTPS host.
        await httpsQueueRunner.Initialize();

        HostLifecycleHooks.Register(httpsHost, restartStopWatch);

        _logger.LogInformation("HTTPS server starting...");
        return await RunHost(httpsHost);
    }

    public async Task<bool> RunHost(WebApplication host)
    {
        IShutdownCoordinator shutdownCoordinator =
            host.Services.GetRequiredService<IShutdownCoordinator>();
        try
        {
            await host.RunAsync(shutdownCoordinator.Token);
        }
        catch (IOException ex)
            when (ex.InnerException is SocketException
                || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
            )
        {
            bool shouldRetry = await _portManager.HandlePortInUse(
                RuntimeServerSettings.Current.InternalServerPort,
                ex
            );
            await host.DisposeAsync();
            return shouldRetry;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Shutdown completed");
        }

        return false;
    }
}
