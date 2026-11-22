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

using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Networking.Cast;
using NoMercy.Setup.Cast;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class CastLaunchLoggingTests
{
    private const string TargetIp = "192.0.2.50";
    private const string ReceiverName = "Test Receiver";

    private static (
        CastPanelWakeLauncher Launcher,
        Mock<IChromeCastService> ChromeCast,
        CapturingLogger<CastPanelWakeLauncher> Log
    ) NewLauncher()
    {
        Mock<IChromeCastService> chromeCast = new();
        CapturingLogger<CastPanelWakeLauncher> log = new();
        return (new(chromeCast.Object, log), chromeCast, log);
    }

    [Fact]
    public async Task LaunchIfColdAsync_LaunchThrows_LogsErrorWithCauseAndReturnsFailed()
    {
        (
            CastPanelWakeLauncher launcher,
            Mock<IChromeCastService> chromeCast,
            CapturingLogger<CastPanelWakeLauncher> log
        ) = NewLauncher();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TargetIp)).ReturnsAsync(ReceiverName);
        chromeCast
            .Setup(c =>
                c.LaunchAndroidReceiver(
                    ReceiverName,
                    It.IsAny<LaunchCustomData?>(),
                    It.IsAny<bool>()
                )
            )
            .ThrowsAsync(new TimeoutException("receiver did not answer"));

        CastLaunchOutcome outcome = await launcher.LaunchIfColdAsync(
            false,
            TargetIp,
            true,
            () => Task.FromResult<LaunchCustomData?>(null)
        );

        outcome.Should().Be(CastLaunchOutcome.Failed);
        log.Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains("TimeoutException")
                && entry.Message.Contains("receiver did not answer")
                && entry.Message.Contains(TargetIp)
            );
    }

    [Fact]
    public async Task LaunchIfColdAsync_NoAddress_LogsWarningAndReturnsNoAddress()
    {
        (CastPanelWakeLauncher launcher, _, CapturingLogger<CastPanelWakeLauncher> log) =
            NewLauncher();

        CastLaunchOutcome outcome = await launcher.LaunchIfColdAsync(
            false,
            null,
            true,
            () => Task.FromResult<LaunchCustomData?>(null)
        );

        outcome.Should().Be(CastLaunchOutcome.NoAddress);
        log.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
        log.Entries.Should().NotContain(entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task LaunchIfColdAsync_NoReceiverDiscovered_LogsWarningAndReturnsNoReceiver()
    {
        (
            CastPanelWakeLauncher launcher,
            Mock<IChromeCastService> chromeCast,
            CapturingLogger<CastPanelWakeLauncher> log
        ) = NewLauncher();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TargetIp)).ReturnsAsync((string?)null);

        CastLaunchOutcome outcome = await launcher.LaunchIfColdAsync(
            false,
            TargetIp,
            true,
            () => Task.FromResult<LaunchCustomData?>(null)
        );

        outcome.Should().Be(CastLaunchOutcome.NoReceiver);
        log.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(true, "cast connect app")]
    [InlineData(false, "web receiver")]
    public async Task LaunchIfColdAsync_Launched_LogsRequestWithTransportAndResult(
        bool useAndroidReceiver,
        string transport
    )
    {
        (
            CastPanelWakeLauncher launcher,
            Mock<IChromeCastService> chromeCast,
            CapturingLogger<CastPanelWakeLauncher> log
        ) = NewLauncher();
        chromeCast.Setup(c => c.FindReceiverNameByIpAsync(TargetIp)).ReturnsAsync(ReceiverName);

        CastLaunchOutcome outcome = await launcher.LaunchIfColdAsync(
            false,
            TargetIp,
            useAndroidReceiver,
            () => Task.FromResult<LaunchCustomData?>(null)
        );

        outcome.Should().Be(CastLaunchOutcome.Launched);
        log.Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("launch requested")
                && entry.Message.Contains(transport)
                && entry.Message.Contains(TargetIp)
            );
        log.Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Information
                && entry.Message.Contains("launch result: launched")
            );
    }
}
