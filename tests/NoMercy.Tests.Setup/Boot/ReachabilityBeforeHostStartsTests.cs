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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Networking.Certificate;
using NoMercy.Networking.Connectivity;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Networking;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using Xunit;

namespace NoMercy.Tests.Setup.Boot;

[Trait("Category", "Unit")]
public sealed class ReachabilityBeforeHostStartsTests
{
    private sealed class FakeChecker(Func<CancellationToken, Task<ReachabilityReport>> run)
        : IReachabilityChecker
    {
        public int BootChecks { get; private set; }
        public CancellationToken SeenToken { get; private set; }

        public Task<ReachabilityReport> CheckBeforeHostStartsAsync(CancellationToken ct)
        {
            BootChecks++;
            SeenToken = ct;
            return run(ct);
        }

        public Task<ReachabilityReport> CheckAsync(CancellationToken ct) =>
            throw new InvalidOperationException("boot must not use the hosted check");

        public ReachabilityReport? TakeBootReport() => null;
    }

    private static BootOrchestrator BuildOrchestrator(IReachabilityChecker? checker) =>
        new(
            new SetupState(),
            new AuthManager(new(), new LocalStorageDriver(), new AuthTokenStore()),
            Mock.Of<IApiKeyLoader>(),
            Mock.Of<IDegradedModeRecovery>(),
            Mock.Of<IServerRegistrationService>(),
            new AuthTokenStore(),
            new CertificateService(NullLogger<CertificateService>.Instance, null!),
            Mock.Of<IHttpClientFactory>(),
            checker
        );

    private static readonly ReachabilityReport Reached = new(
        ReachabilityVerdict.Reachable,
        ReachabilityVerdict.Unknown,
        DateTime.UtcNow
    );

    [Fact]
    public async Task BootRunsTheCheckOnce_WithADeadline()
    {
        FakeChecker checker = new(_ => Task.FromResult(Reached));

        await BuildOrchestrator(checker).CheckReachabilityBeforeHostStartsAsync(CancellationToken.None);

        // A check without a deadline could hold the port, and the whole boot, forever.
        Assert.Equal(1, checker.BootChecks);
        Assert.True(checker.SeenToken.CanBeCanceled);
    }

    [Fact]
    public async Task ACheckCutOffByTheDeadline_DoesNotFailBoot()
    {
        FakeChecker checker = new(ct =>
            Task.FromException<ReachabilityReport>(new OperationCanceledException(ct))
        );

        Exception? ex = await Record.ExceptionAsync(() =>
            BuildOrchestrator(checker).CheckReachabilityBeforeHostStartsAsync(CancellationToken.None)
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task ACheckThatThrows_DoesNotFailBoot()
    {
        FakeChecker checker = new(_ =>
            Task.FromException<ReachabilityReport>(new InvalidOperationException("boom"))
        );

        Exception? ex = await Record.ExceptionAsync(() =>
            BuildOrchestrator(checker).CheckReachabilityBeforeHostStartsAsync(CancellationToken.None)
        );

        Assert.Null(ex);
    }

    [Fact]
    public async Task ShutdownDuringTheCheck_IsNotSwallowed()
    {
        using CancellationTokenSource shutdown = new();
        await shutdown.CancelAsync();
        FakeChecker checker = new(ct =>
            Task.FromException<ReachabilityReport>(new OperationCanceledException(ct))
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BuildOrchestrator(checker).CheckReachabilityBeforeHostStartsAsync(shutdown.Token)
        );
    }

    [Fact]
    public async Task WithoutAChecker_BootSkipsTheStep()
    {
        Exception? ex = await Record.ExceptionAsync(() =>
            BuildOrchestrator(null).CheckReachabilityBeforeHostStartsAsync(CancellationToken.None)
        );

        Assert.Null(ex);
    }
}
