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

using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Database;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Lifecycle;
using NoMercy.NmSystem.Security;
using NoMercy.Service.Hosting;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Common;
using NoMercy.Tests.Setup.Infrastructure;
using NoMercyQueue;

namespace NoMercy.Tests.Setup.Boot;

/// <summary>
/// The library/import queues only start once <see cref="BootStage.All"/>
/// (Essential | Auth | Binaries | Network | Registered) is reached. On the
/// interactive first boot, <see cref="ServerRunner.RunWithHttpsRestart"/> ran the
/// post-auth work by calling <see cref="BootOrchestrator.RunRegistrationAsync"/>
/// directly, which marks ONLY <see cref="BootStage.Registered"/>.
/// <see cref="BootOrchestrator.RunPostAuthAsync"/> — the method that additionally
/// marks <see cref="BootStage.Auth"/> and kicks off the background tasks that mark
/// Binaries and Network — was never called from production code at all.
///
/// Live consequence (v0.1.450, verified against the real dev stack): a brand-new
/// user finished setup, added three libraries, and every scan job sat queued
/// forever with no error shown. ffmpeg was never downloaded either. Only an
/// unprompted server restart — which takes the already-authenticated branch —
/// unblocked it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FirstBootReachesAllStagesTests : IDisposable
{
    private readonly AppDbContext _appContext;
    private readonly SetupState _setupState = new();
    private readonly BootOrchestrator _orchestrator;

    public FirstBootReachesAllStagesTests()
    {
        ResetSharedTracker();

        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        TokenStore.Initialize(services.BuildServiceProvider());

        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");
        _appContext = new(optionsBuilder.Options);
        _appContext.Database.OpenConnection();
        _appContext.Database.EnsureCreated();

        _orchestrator = new(
            _setupState,
            new(_appContext, new LocalStorageDriver(), new AuthTokenStore()),
            new FakeApiKeyLoader(),
            new FakeDegradedModeRecovery(),
            new FakeServerRegistrationService(),
            new AuthTokenStore(),
            new CertificateService(NullLogger<CertificateService>.Instance, null!),
            new RealHttpClientFactory()
        );
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    public void Dispose()
    {
        _appContext.Database.CloseConnection();
        _appContext.Dispose();
        ResetSharedTracker();
    }

    // ServerPhaseTracker is a process-wide singleton so the HTTPS-restart rebuild
    // keeps one gate; that leaks across xUnit tests in the same AppDomain unless
    // it is reset per test.
    private static void ResetSharedTracker()
    {
        MethodInfo reset = typeof(ServerPhaseTracker).GetMethod(
            "ResetSharedForTests",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        reset.Invoke(null, null);
    }

    private static IServerPhaseTracker FreshTracker()
    {
        ServerPhaseTracker tracker = ServerPhaseTracker.Shared();
        // ServerBootstrapper marks Essential before the setup UI is ever shown.
        tracker.MarkComplete(BootStage.Essential);
        return tracker;
    }

    private void DriveToAuthenticated()
    {
        _setupState.TransitionTo(SetupPhase.Authenticating);
        _setupState.TransitionTo(SetupPhase.Authenticated);
    }

    /// <summary>
    /// Pins the actual defect: the bare registration call leaves Auth unmarked, so
    /// BootStage.All can never be reached no matter what else succeeds. This is what
    /// the interactive first boot used to run.
    /// </summary>
    [Fact]
    public async Task RunRegistrationAsync_Alone_LeavesAuthUnmarked_SoAllIsUnreachable()
    {
        IServerPhaseTracker tracker = FreshTracker();
        DriveToAuthenticated();

        await _orchestrator.RunRegistrationAsync(CancellationToken.None);

        Assert.True(tracker.IsComplete(BootStage.Registered));
        Assert.False(tracker.IsComplete(BootStage.Auth));
        Assert.False(tracker.IsComplete(BootStage.All));
    }

    /// <summary>
    /// The contract the interactive first boot depends on: completing setup must
    /// mark Auth, so that with Binaries + Network (marked by the background tasks
    /// this path starts) BootStage.All becomes reachable and the library queue runs.
    /// </summary>
    [Fact]
    public async Task RunPostAuthAsync_MarksAuth_SoAllBecomesReachable()
    {
        IServerPhaseTracker tracker = FreshTracker();
        DriveToAuthenticated();

        await _orchestrator.RunPostAuthAsync(CancellationToken.None);

        Assert.True(tracker.IsComplete(BootStage.Auth));
        Assert.True(tracker.IsComplete(BootStage.Registered));

        // Binaries + Network are marked by Start.InitRemaining, which this path
        // starts in the background (and which needs real binaries/network, so it
        // is not awaited here). Simulate their completion to prove that Auth was
        // the only thing standing between this path and BootStage.All.
        tracker.MarkComplete(BootStage.Binaries);
        tracker.MarkComplete(BootStage.Network);

        Assert.True(tracker.IsComplete(BootStage.All));
    }

    /// <summary>
    /// Guards the wiring itself. The two methods above are both correct in
    /// isolation — the live failure was ServerRunner choosing the wrong one, so
    /// pin that the interactive setup path calls the post-auth entry point.
    /// </summary>
    [Fact]
    public void ServerRunner_InteractiveSetupPath_UsesPostAuthEntryPoint()
    {
        string source = ReadSource("src/NoMercy.Service/Hosting/ServerRunner.cs");

        Assert.Contains("RunPostAuthAsync", source);
        Assert.DoesNotContain("orchestrator.RunRegistrationAsync", source);
    }

    /// <summary>
    /// The setup host's queue workers must be stopped before its container is disposed.
    /// They poll a queue that outlives the host, so a worker left running scopes every
    /// job it reserves against a dead provider — 367 jobs dead-lettered on a measured
    /// first boot, including the scan for a library added after the restart.
    /// <see cref="QueueRunner.StopAll"/> existed for exactly this and had no callers.
    /// </summary>
    [Fact]
    public void ServerRunner_StopsTheSetupHostsQueueWorkers_BeforeDisposingIt()
    {
        string source = ReadSource("src/NoMercy.Service/Hosting/ServerRunner.cs");

        // Anchor on the restart itself — the file disposes httpHost on other paths too,
        // and only the ordering inside this block is the thing being pinned.
        int restart = source.IndexOf(
            "Certificate ready — restarting with HTTPS",
            StringComparison.Ordinal
        );
        Assert.True(restart >= 0, "the HTTPS restart block moved — this guard needs updating");

        int stopAll = source.IndexOf("StopAll()", restart, StringComparison.Ordinal);
        int dispose = source.IndexOf("httpHost.DisposeAsync()", restart, StringComparison.Ordinal);

        Assert.True(stopAll >= 0, "the HTTPS restart must stop the setup host's workers");
        Assert.True(
            stopAll < dispose,
            "workers must be stopped before the container they scope jobs against is disposed"
        );
    }

    private static string ReadSource(string relativePath)
    {
        return File.ReadAllText(RepoPaths.At(relativePath));
    }
}
