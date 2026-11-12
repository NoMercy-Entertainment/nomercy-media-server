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
using NoMercy.Launcher.Services;
using Xunit;

namespace NoMercy.Tests.Launcher.Services;

public sealed class ServerProcessLauncherSupervisionTests
{
    private readonly List<ProcessStartInfo> _spawned = [];

    [Fact]
    public void PrepareServerStartInfo_MarksTheChildAsLauncherSupervised()
    {
        ProcessStartInfo startInfo = new("server") { UseShellExecute = false };

        ServerProcessLauncher.PrepareServerStartInfo(startInfo, "--dev --port 7626", null);

        startInfo.Environment["NOMERCY_SUPERVISOR"].Should().Be("launcher");
        startInfo.ArgumentList.Should().Equal("--dev", "--port", "7626");
    }

    [Fact]
    public async Task ExitWithRestartCode_StartsTheServerAgainWithTheSameArguments()
    {
        ServerProcessLauncher launcher = RecordingLauncher(serverAnswers: false);
        ProcessStartInfo startInfo = new("server") { UseShellExecute = false };
        ServerProcessLauncher.PrepareServerStartInfo(startInfo, "--dev --port 7626", null);
        await launcher.LaunchServerAsync(startInfo);

        bool restarted = await launcher.RestartAfterExitAsync(75);

        restarted.Should().BeTrue();
        _spawned.Should().HaveCount(2);
        _spawned[1].FileName.Should().Be("server");
        _spawned[1].ArgumentList.Should().Equal("--dev", "--port", "7626");
        _spawned[1].Environment["NOMERCY_SUPERVISOR"].Should().Be("launcher");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(137)]
    public async Task ExitWithAnyOtherCode_DoesNotStartTheServerAgain(int exitCode)
    {
        ServerProcessLauncher launcher = RecordingLauncher(serverAnswers: false);
        await launcher.LaunchServerAsync(new("server") { UseShellExecute = false });

        bool restarted = await launcher.RestartAfterExitAsync(exitCode);

        restarted.Should().BeFalse();
        _spawned.Should().HaveCount(1);
    }

    [Fact]
    public async Task StartServerAsync_WhenAServerAlreadyAnswers_DoesNotSpawn()
    {
        ServerProcessLauncher launcher = RecordingLauncher(serverAnswers: true);

        bool started = await launcher.StartServerAsync("--dev");

        started.Should().BeFalse();
        _spawned.Should().BeEmpty();
    }

    private ServerProcessLauncher RecordingLauncher(bool serverAnswers) =>
        new(
            () => Task.FromResult(serverAnswers),
            startInfo =>
            {
                _spawned.Add(startInfo);
                return Task.FromResult(true);
            }
        );
}
