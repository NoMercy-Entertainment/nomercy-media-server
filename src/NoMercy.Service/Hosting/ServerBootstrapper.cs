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
using System.Reflection;
using NoMercy.Networking.Cast;
using NoMercy.Networking.Certificate;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Service.Seeds;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Setup.Ui;
using NoMercy.Storage;
using NoMercyQueue;

namespace NoMercy.Service.Hosting;

public sealed class ServerBootstrapper
{
    public async Task RunAsync(StartupOptions options)
    {
        switch (options.RunAsService)
        {
            case true:
            {
                // When running as a service, the working directory may not be the executable's directory.
                // Windows services start in system32; systemd services start in /.
                // Set it to the executable's directory so config and data paths resolve correctly.
                string exeDir = AppContext.BaseDirectory;
                Directory.SetCurrentDirectory(exeDir);

                string platform =
                    Software.IsWindows ? "Windows service"
                    : Software.IsLinux ? "systemd service"
                    : Software.IsMac ? "launchd service"
                    : "service";
                Logger.App($"Running as {platform}, content root: {exeDir}");
                break;
            }
            case false when !Console.IsOutputRedirected:
                Console.Clear();
                Console.Title = AppFiles.ApplicationName;
                break;
        }

        if (!options.RunAsService)
            ConsoleMessages.Logo();

        options.ApplySettings();

        Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        Software.Version = version;
        Logger.App(
            $"NoMercy MediaServer version: v{version.Major}.{version.Minor}.{version.Build}"
        );

        Stopwatch stopWatch = new();
        stopWatch.Start();

        // Route storage-facade temp + transcode writes inside the NoMercy data
        // directory instead of the OS temp folder. StoragePaths default to
        // Path.GetTempPath(); the orchestrator + remote storage stage files
        // there. Encoder gets its own 'cache/encoder' subdir so transcodes
        // don't share scratch space with the rest of the lease churn.
        StoragePaths.TempRoot = AppFiles.TempPath;
        StoragePaths.TranscodeRoot = AppFiles.EncoderCachePath;

        // Pre-DI storage pair — used for seed calls that run before the DI
        // container is built. Same pattern as Start.cs Binaries task.
        (IStorage preBootStorage, IStorageDriver preBootBackend) = BootstrapStorageFactory.Create();

        // Folders, then schema, then Phase 1 — in that order. Phase 1's
        // UserSettings task reads the Configuration table, so on a fresh install
        // the schema must already exist or every first boot opens with a
        // "no such table: Configuration" error and silently discards defaults.
        // CreateAppFolders is idempotent; Phase 1 re-running it is harmless.
        await AppFiles.CreateAppFolders();

        // Create a database schema before anything else can query it.
        // This does NOT require auth — only migrations + EnsureCreated.
        await DatabaseSeeder.InitSchema(preBootStorage);

        // Phase 1 (UserSettings, CreateAppFolders, ApiInfo) — fast, no network
        await Start.InitEssential();

        // Seed offline data (config, languages, encoder profiles, etc.)
        // immediately so the UI has data before auth completes.
        await DatabaseSeeder.SeedOfflineData(preBootStorage, preBootBackend);

        // Use certificate presence for the initial forceHttp decision — this is a
        // filesystem check that doesn't require DI. BootOrchestrator (resolved below)
        // will own the real needsSetupMode determination via token validation.
        bool hasCert;
        await using (
            ServiceProvider certPresenceProvider = new ServiceCollection()
                .AddHttpClient()
                .AddSingleton<ICertificateService, CertificateService>()
                .BuildServiceProvider()
        )
        {
            hasCert = certPresenceProvider
                .GetRequiredService<ICertificateService>()
                .HasValidCertificate();
        }

        WebApplication app = WebHostFactory.Create(options, forceHttp: !hasCert);

        // Resolve the cert service once so its boot handle (Start.Certificate) is set
        // before any runtime consumer (ServerRunner, SetupEndpoints) needs it.
        _ = app.Services.GetRequiredService<ICertificateService>();

        IShutdownCoordinator shutdownCoordinator =
            app.Services.GetRequiredService<IShutdownCoordinator>();
        IPortManager portManager = app.Services.GetRequiredService<IPortManager>();

        IApiKeyLoader apiKeyLoader = app.Services.GetRequiredService<IApiKeyLoader>();
        await apiKeyLoader.LoadKeys(shutdownCoordinator.Token);

        // Proactively resolve port conflicts before proceeding.
        // This avoids the costly build→fail→kill→rebuild cycle and prevents
        // CronWorker "Failed to start database job workers" errors.
        await portManager.EnsurePortAvailable(RuntimeServerSettings.Current.InternalServerPort);

        // Hand the phase tracker to the static accessor so boot helpers in
        // NoMercy.Setup (Start.cs, Binaries.cs) can advance stages without DI
        // plumbing. Phase 1 (essentials) already completed pre-DI — mark it now.
        NmSystem.Lifecycle.ServerPhaseTracker.RegisterCurrent(
            app.Services.GetRequiredService<NmSystem.Lifecycle.IServerPhaseTracker>()
        );
        NmSystem.Lifecycle.ServerPhaseTracker.Current?.MarkComplete(
            NmSystem.Lifecycle.BootStage.Essential
        );

        // From this point on, use the DI-registered storage singletons.
        IStorage diStorage = app.Services.GetRequiredService<IStorage>();
        IStorageDriver dIStorageDriver = app.Services.GetRequiredService<IStorageDriver>();

        // API keys are available without auth, so seed TMDB/MusicBrainz data
        // (genres, languages, etc.) now — before any import jobs can run.
        // Must run AFTER CreateWebApplication so HttpClientProvider is bound
        // to the real IHttpClientFactory (otherwise seed HTTP calls fall back
        // to a bare HttpClient with no registered headers, and MusicBrainz
        // returns 403 for anonymous UAs).
        await DatabaseSeeder.Run(diStorage, dIStorageDriver);

        // Rename on-disk bundle directories when a built-in preset slug changed.
        await DatabaseSeeder.RunBundleSlugRenamePassAsync(
            app.Services.GetRequiredService<IStorageFactory>(),
            app.Services.GetRequiredService<ILogger<Encoder.Bundle.BundleSlugRenamer>>()
        );

        // BootOrchestrator owns Phase 2 (auth) and Phase 3 (registration).
        // It returns true when interactive auth is required (setup mode).
        BootOrchestrator orchestrator = app.Services.GetRequiredService<BootOrchestrator>();
        bool needsSetupMode = await orchestrator.RunAsync(app.Services, shutdownCoordinator.Token);

        // The initial forceHttp decision used cert presence as a proxy for "auth done".
        // It's wrong when a cert exists but tokens are missing/unreadable (DataProtection
        // key rotation, manual wipe, schema migration) — Kestrel binds HTTPS-only but the
        // setup browser URL is plain HTTP, so the browser gets ERR_EMPTY_RESPONSE. Rebuild
        // the host as HTTP-only for setup flow, then let BootOrchestrator handle the real needsSetupMode determination.
        if (needsSetupMode && hasCert)
        {
            Logger.App(
                "Setup required but host is HTTPS-bound — rebuilding as HTTP-only for setup flow"
            );
            await app.DisposeAsync();
            app = WebHostFactory.Create(options, forceHttp: true);
            diStorage = app.Services.GetRequiredService<IStorage>();
            orchestrator = app.Services.GetRequiredService<BootOrchestrator>();

            // The disposed container took its ShutdownCoordinator (and the
            // CancellationTokenSource behind shutdownCoordinator.Token) with it.
            // Rebind to the new container so later consumers don't touch a
            // disposed token source.
            shutdownCoordinator = app.Services.GetRequiredService<IShutdownCoordinator>();
        }

        // Load SSL cert from database now that TokenStore is initialized by BootOrchestrator
        Start.Certificate!.LoadFromDb();

        // Inverse of the setup rebuild above: the host was built HTTP-only because
        // no certificate existed when the forceHttp decision was made, but
        // BootOrchestrator.RunAsync just registered the server and acquired one
        // (the has-token-no-cert first boot). Without this the server keeps serving
        // plain HTTP until a manual restart. Rebind to HTTPS now — the host has not
        // started yet, so this is a clean pre-start swap, not a live restart.
        //
        // EnsureHttpsCertificate() (not HasValidCertificate()) so an already-registered
        // server whose real LE cert is still missing/slow/rate-limited gets the
        // self-signed fallback instead of running HTTP-only indefinitely — the dashboard
        // (HTTPS-only) can otherwise never reach this origin at all.
        if (!needsSetupMode && !hasCert && Start.Certificate!.EnsureHttpsCertificate())
        {
            Logger.App("Certificate ready — rebinding host to HTTPS");
            await app.DisposeAsync();
            app = WebHostFactory.Create(options);
            diStorage = app.Services.GetRequiredService<IStorage>();
            shutdownCoordinator = app.Services.GetRequiredService<IShutdownCoordinator>();

            // Resolve the cert service on the new container (this sets the
            // Start.Certificate boot handle to the new singleton) and load the
            // freshly acquired cert into its cache so Kestrel's certificate
            // selector has it on the first TLS handshake.
            _ = app.Services.GetRequiredService<ICertificateService>();
            Start.Certificate!.LoadFromDb();

            // AuthTokenStore is a per-container singleton and AuthManager is its only
            // writer, so the fresh container starts with a null access token. Everything
            // that waits on authentication then gives up permanently — ConnectivityManager
            // waits 30s, logs "skipped — no authentication available" and returns without
            // ever evaluating a transport, which on a tunnel-only connection means no
            // remote access for the life of the process. ServerRunner's equivalent rebuild
            // already does this; this branch did not.
            AuthManager rebuiltAuthManager = app.Services.GetRequiredService<AuthManager>();
            await rebuiltAuthManager.InitializeAsync();
            rebuiltAuthManager.ScheduleBackgroundRefresh(shutdownCoordinator.Token);

            // SetupState is a per-container singleton. BootOrchestrator.RunAsync
            // already drove the OLD container's SetupState to Complete (that's
            // what made needsSetupMode false) but that instance was just disposed
            // with the container — the new container's SetupState starts fresh at
            // Unauthenticated. Without this, SetupModeMiddleware would 503 every
            // route on the new container even though auth + registration + the
            // certificate we just acquired are all genuinely done. Restore the
            // new container's SetupState to Complete to match the reality the
            // orchestrator already established.
            app.Services.GetRequiredService<SetupState>()
                .DetermineInitialPhase(hasValidToken: true, isRegistered: true);
        }

        // Auth completed — seed auth-dependent data (users, library assignment, claims)
        if (!needsSetupMode)
            await DatabaseSeeder.SeedAuthData(
                diStorage,
                app.Services.GetRequiredService<IAuthTokenStore>().AccessToken
            );

        // Force QueueRunner singleton creation and initialize workers immediately —
        // don't wait for InitRemaining() which can be blocked by rate-limited HTTP calls.
        QueueRunner queueRunner = app.Services.GetRequiredService<QueueRunner>();
        await queueRunner.Initialize();

        // Scan and load plugins from the plugins directory. Missing directory is safe —
        // returns empty list and logs INFO. One plugin's failure never blocks others.
        IPluginLoader pluginLoader = app.Services.GetRequiredService<IPluginLoader>();
        IReadOnlyList<PluginLoadResult> loadedPlugins = await pluginLoader.LoadPlugins(
            shutdownCoordinator.Token
        );

        HostLifecycleHooks.Register(app, stopWatch);

        // Log addresses and run dev tasks after the host is live.
        // InitRemaining is now owned by BootOrchestrator — only host-level
        // post-startup concerns belong here.
        _ = Task.Run(async () =>
        {
            // Eagerly resolve the cast service so its boot handle (Start.ChromeCast) is populated.
            _ = app.Services.GetService<IChromeCastService>();
            INetworkDiscovery? networkDiscovery = app.Services.GetService<INetworkDiscovery>();
            if (networkDiscovery is not null)
            {
                Logger.App($"Internal Address: {networkDiscovery.InternalAddress}");
                if (
                    !string.IsNullOrEmpty(networkDiscovery.ExternalIp)
                    && networkDiscovery.ExternalIp != "0.0.0.0"
                )
                    Logger.App($"External Address: {networkDiscovery.ExternalAddress}");
                if (networkDiscovery.ExternalAddressV6 is not null)
                    Logger.App($"External IPv6 Address: {networkDiscovery.ExternalAddressV6}");
            }

            if (!options.RunAsService && !Console.IsOutputRedirected)
                await ConsoleMessages.ServerRunning();

            Logger.App($"Server started in {stopWatch.ElapsedMilliseconds}ms");

            await Dev.Run();
        });

        IServerRunner serverRunner = app.Services.GetRequiredService<IServerRunner>();

        bool shouldRetry;
        if (needsSetupMode)
        {
            Logger.App("Starting in HTTP mode — waiting for setup completion...");
            shouldRetry = await serverRunner.RunWithHttpsRestart(app, options, orchestrator);
        }
        else
        {
            shouldRetry = await serverRunner.RunHost(app);
        }

        if (shouldRetry)
        {
            // RunHost/RunWithHttpsRestart already disposed the host (and the
            // CancellationTokenSource behind shutdownCoordinator) before signalling a
            // retry, so we must NOT touch shutdownCoordinator here — the retry host
            // below builds its own. Calling RequestShutdown() on the disposed
            // coordinator threw ObjectDisposedException on the port-conflict path.
            Logger.App("Rebuilding server after port conflict resolution...");

            Stopwatch retryStopWatch = new();
            retryStopWatch.Start();

            WebApplication retryHost = WebHostFactory.Create(options, forceHttp: needsSetupMode);
            HostLifecycleHooks.Register(retryHost, retryStopWatch);

            IServerRunner retryServerRunner =
                retryHost.Services.GetRequiredService<IServerRunner>();

            // Force the DI container to instantiate QueueRunner (it's a lazy singleton).
            QueueRunner retryQueueRunner = retryHost.Services.GetRequiredService<QueueRunner>();

            // Initialize queue workers so they can process jobs on the retry host.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    Logger.App("Initializing QueueRunner for retry host...");
                    await retryQueueRunner.Initialize();
                    Logger.App("QueueRunner initialized for retry host");
                }
                catch (Exception ex)
                {
                    Logger.App($"Failed to initialize QueueRunner for retry host: {ex}");
                }
            });

            if (needsSetupMode)
            {
                // Re-enter the setup/certificate flow so the server can complete
                // first-boot setup rather than just running without HTTPS support.
                // Resolve a fresh orchestrator from the retry host's DI container.
                BootOrchestrator retryOrchestrator =
                    retryHost.Services.GetRequiredService<BootOrchestrator>();
                await retryServerRunner.RunWithHttpsRestart(retryHost, options, retryOrchestrator);
            }
            else
            {
                await retryServerRunner.RunHost(retryHost);
            }
        }
    }
}
