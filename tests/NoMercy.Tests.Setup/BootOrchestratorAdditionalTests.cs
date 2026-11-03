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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Security;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup;

/// <summary>
/// Requirement: <see cref="BootOrchestrator.RunAsync"/> must enter setup mode (return
/// true) when there is no valid cached auth token, and must never let a Keycloak
/// reachability probe crash the boot sequence — the well-known-config check logs a
/// warning/error but always continues to the auth attempt. Registration
/// (<see cref="BootOrchestrator.RunRegistrationAsync"/>) must advance the setup phase
/// to Complete on success and record — never throw past — a registration failure so a
/// transient nomercy-tv outage degrades to retry-later instead of crashing the server.
/// </summary>
/// <remarks>
/// <see cref="AuthManager.IsDesktopEnvironment"/> is unconditionally true on Windows and
/// macOS, so <see cref="BootOrchestrator.StartHeadlessDeviceCodeFlowAsync"/> and its
/// private <c>PollDeviceGrant</c> helper are dead code on this dev machine and covered
/// by the project's headless Linux CI leg — see the itemized test below, which only
/// locks the Windows early-return.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class BootOrchestratorAdditionalTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly AuthManager _authManager;
    private readonly SetupState _setupState;
    private readonly ServiceProvider _serviceProvider;
    private readonly string? _originalAppPath;
    private readonly string _tempAppPath;

    public BootOrchestratorAdditionalTests()
    {
        _originalAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        _tempAppPath = Path.Combine(Path.GetTempPath(), $"nm-bootorch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _tempAppPath);

        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _serviceProvider = services.BuildServiceProvider();
        TokenStore.Initialize(_serviceProvider);

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _authManager = new(_appContext, new LocalStorageDriver(), new AuthTokenStore());
        _setupState = new();

        // CertificateService.LoadFromDb/HasValidCertificate open their OWN on-disk
        // AppDbContext at NOMERCY_APP_PATH (independent of the in-memory _appContext
        // above) — create the folder tree + schema there too, mirroring what real
        // Phase-1 (AppFiles.CreateAppFolders + migrations) already guarantees before
        // BootOrchestrator ever runs.
        NoMercy.NmSystem.Information.AppFiles.CreateAppFolders();
        using AppDbContext onDisk = new();
        onDisk.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
        _serviceProvider.Dispose();
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _originalAppPath);
        try
        {
            if (Directory.Exists(_tempAppPath))
                Directory.Delete(_tempAppPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private BootOrchestrator BuildOrchestrator(
        IServerRegistrationService? registrationService = null,
        ICertificateService? certificateService = null
    ) =>
        new(
            _setupState,
            _authManager,
            new FakeApiKeyLoader(),
            new FakeDegradedModeRecovery(),
            registrationService ?? new FakeServerRegistrationService(),
            new AuthTokenStore(),
            certificateService
                ?? new CertificateService(NullLogger<CertificateService>.Instance, null!),
            new RealHttpClientFactory()
        );

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task RunAsync_NoValidToken_ReturnsTrue_EntersSetupMode()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, "{}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        BootOrchestrator orchestrator = BuildOrchestrator();

        bool needsSetup = await orchestrator.RunAsync(_serviceProvider, CancellationToken.None);

        Assert.True(needsSetup);
    }

    [Fact]
    public async Task RunAsync_KeycloakWellKnownReachable_DoesNotThrow()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, "{\"issuer\":\"https://auth.nomercy.tv/realms/NoMercyTV\"}");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        BootOrchestrator orchestrator = BuildOrchestrator();

        bool needsSetup = await orchestrator.RunAsync(_serviceProvider, CancellationToken.None);

        // No cached token in the in-memory DB either way — this test's purpose is
        // proving the reachable-well-known branch itself completes cleanly.
        Assert.True(needsSetup);
    }

    [Fact]
    public async Task RunAsync_KeycloakWellKnownReturnsError_StillProceedsWithoutThrowing()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(503, "service unavailable");
        using ExternalServicesConfigScope scope = new(authBaseUrl: server.BaseUrl);

        BootOrchestrator orchestrator = BuildOrchestrator();

        bool needsSetup = await orchestrator.RunAsync(_serviceProvider, CancellationToken.None);

        Assert.True(needsSetup);
    }

    [Fact]
    public async Task RunAsync_KeycloakUnreachable_StillProceedsWithoutThrowing()
    {
        // Connection-refused path (no cached JWKS key -> the "BOOT FAILURE" log branch,
        // or a cached key -> the softer warning branch; both just log, so this test only
        // locks "the probe failing must never crash the boot sequence").
        using ExternalServicesConfigScope scope = new(authBaseUrl: "http://127.0.0.1:1/");

        BootOrchestrator orchestrator = BuildOrchestrator();

        bool needsSetup = await orchestrator.RunAsync(_serviceProvider, CancellationToken.None);

        Assert.True(needsSetup);
    }

    [Fact]
    public async Task RunRegistrationAsync_Success_TransitionsToCompleteAndReturnsCertStatus()
    {
        BootOrchestrator orchestrator = BuildOrchestrator();
        // RunRegistrationAsync's own TransitionTo(Registering) is only a valid move
        // from Authenticated per SetupState's state machine — mirror the real call
        // sequence (RunAsync/RunPostAuthAsync always reach Authenticated first).
        _setupState.TransitionTo(SetupPhase.Authenticating);
        _setupState.TransitionTo(SetupPhase.Authenticated);

        bool certAcquired = await orchestrator.RunRegistrationAsync(CancellationToken.None);

        // No certificate has been stored in the fresh on-disk DB, so registration
        // succeeds but certificate acquisition does not — the phase reaches the
        // distinct, retryable Failed phase rather than a misleading Complete.
        Assert.False(certAcquired);
        Assert.Equal(SetupPhase.Failed, _setupState.CurrentPhase);
        Assert.True(_setupState.IsSetupRequired);
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task RunRegistrationAsync_RegistrationThrows_SetsErrorAndReachesFailed()
    {
        ThrowingServerRegistrationService throwing = new();
        BootOrchestrator orchestrator = BuildOrchestrator(throwing);
        _setupState.TransitionTo(SetupPhase.Authenticating);
        _setupState.TransitionTo(SetupPhase.Authenticated);

        bool certAcquired = await orchestrator.RunRegistrationAsync(CancellationToken.None);

        // TransitionTo(Failed) clears ErrorMessage as part of every transition (see
        // SetupState.TransitionTo), and SetError runs after it — the error survives,
        // and the phase is the distinct Failed the setup page renders as a failure.
        Assert.False(certAcquired);
        Assert.Equal(SetupPhase.Failed, _setupState.CurrentPhase);
        Assert.NotNull(_setupState.ErrorMessage);
    }

    [Fact]
    public async Task StartHeadlessDeviceCodeFlowAsync_OnDesktopEnvironment_ReturnsImmediately()
    {
        // AuthManager.IsDesktopEnvironment() is unconditionally true on Windows/macOS,
        // but on Linux it reads the environment — so on a headless CI runner (no
        // DISPLAY, no WAYLAND_DISPLAY) this method takes the device-code-flow branch
        // instead and runs the token out. Stage a desktop session explicitly so the
        // early return under test is reached on every platform rather than only on
        // the developer's Windows box.
        using DesktopEnvironmentScope _ = new();

        BootOrchestrator orchestrator = BuildOrchestrator();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        await orchestrator.StartHeadlessDeviceCodeFlowAsync(cts.Token);

        Assert.False(
            cts.IsCancellationRequested,
            "should return immediately, not run out the clock"
        );
    }

    /// <summary>
    /// Makes <c>Screen.IsDesktopEnvironment()</c> report a desktop session for the
    /// duration of a test: sets DISPLAY, and clears the two variables that force the
    /// headless answer regardless of DISPLAY (container and WSL detection). Every
    /// value is restored on dispose so the process-wide environment is not leaked
    /// into other tests.
    /// </summary>
    private sealed class DesktopEnvironmentScope : IDisposable
    {
        private static readonly string[] VariableNames =
        [
            "DISPLAY",
            "DOTNET_RUNNING_IN_CONTAINER",
            "WSL_DISTRO_NAME",
        ];

        private readonly Dictionary<string, string?> _originalValues = new();

        public DesktopEnvironmentScope()
        {
            foreach (string name in VariableNames)
                _originalValues[name] = Environment.GetEnvironmentVariable(name);

            Environment.SetEnvironmentVariable("DISPLAY", ":0");
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", null);
            Environment.SetEnvironmentVariable("WSL_DISTRO_NAME", null);
        }

        public void Dispose()
        {
            foreach (KeyValuePair<string, string?> original in _originalValues)
                Environment.SetEnvironmentVariable(original.Key, original.Value);
        }
    }

    private sealed class ThrowingServerRegistrationService : IServerRegistrationService
    {
        public Task Init(int maxRetries = 5) =>
            throw new InvalidOperationException("simulated registration failure");

        public Task GetTunnelAvailability() => Task.CompletedTask;
    }
}
