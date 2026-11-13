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
using NoMercy.NmSystem.SystemCalls;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class ServerRestartPlanTests
{
    private const string WaitPidVariable = "NOMERCY_RESTART_WAIT_PID";

    [Theory]
    [InlineData("INVOCATION_ID", "0123456789abcdef", true)]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER", "true", true)]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER", "1", true)]
    [InlineData("NOMERCY_SUPERVISOR", "launcher", true)]
    [InlineData("XPC_SERVICE_NAME", "tv.nomercy.mediaserver", true)]
    [InlineData("XPC_SERVICE_NAME", "tv.nomercy.mediaserver.service", true)]
    [InlineData("XPC_SERVICE_NAME", "0", false)]
    [InlineData("XPC_SERVICE_NAME", "application.com.jetbrains.rider.12345.67890", false)]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER", "false", false)]
    [InlineData("NOMERCY_SUPERVISOR", "something-else", false)]
    [InlineData("UNRELATED", "value", false)]
    public void IsSupervised_ReadsTheSupervisorMarkers(string name, string value, bool expected)
    {
        Dictionary<string, string?> environment = new() { [name] = value };

        bool supervised = ServerRestartPlan.IsSupervised(environment.GetValueOrDefault);

        supervised.Should().Be(expected);
    }

    [Fact]
    public void CreateRelaunchStartInfo_CarriesTheSameArgumentsFolderAndTheWaitPid()
    {
        ProcessStartInfo startInfo = ServerRestartPlan.CreateRelaunchStartInfo(
            "/opt/nomercy/NoMercyMediaServer",
            ["/opt/nomercy/NoMercyMediaServer", "--dev", "--internal-port", "7626"],
            "/srv/work",
            4242
        );

        startInfo.FileName.Should().Be("/opt/nomercy/NoMercyMediaServer");
        startInfo.ArgumentList.Should().Equal("--dev", "--internal-port", "7626");
        startInfo.WorkingDirectory.Should().Be("/srv/work");
        startInfo.Environment[WaitPidVariable].Should().Be("4242");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.RedirectStandardOutput.Should().BeFalse();
        startInfo.RedirectStandardError.Should().BeFalse();
    }

    [Fact]
    public void CreateRelaunchStartInfo_UnderTheDotnetHost_KeepsTheAssemblyPath()
    {
        ProcessStartInfo startInfo = ServerRestartPlan.CreateRelaunchStartInfo(
            "/usr/share/dotnet/dotnet",
            ["/opt/nomercy/NoMercyMediaServer.dll", "--dev"],
            "/srv/work",
            4242
        );

        startInfo.ArgumentList.Should().Equal("/opt/nomercy/NoMercyMediaServer.dll", "--dev");
    }

    [Fact]
    public void WaitForPreviousServer_ReturnsWhenThePidIsGone_AndClearsTheVariable()
    {
        Dictionary<string, string?> environment = new() { [WaitPidVariable] = "4242" };
        Queue<bool> running = new([true, true, false]);
        List<int> probed = [];
        int pauses = 0;

        bool gone = ServerRestartPlan.WaitForPreviousServer(
            environment.GetValueOrDefault,
            (name, value) => environment[name] = value,
            pid =>
            {
                probed.Add(pid);
                return running.Dequeue();
            },
            _ => pauses++,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(1)
        );

        gone.Should().BeTrue();
        probed.Should().Equal(4242, 4242, 4242);
        pauses.Should().Be(2);
        environment[WaitPidVariable].Should().BeNull();
    }

    [Fact]
    public void WaitForPreviousServer_GivesUpAfterTheCap_AndClearsTheVariable()
    {
        Dictionary<string, string?> environment = new() { [WaitPidVariable] = "4242" };
        int pauses = 0;

        bool gone = ServerRestartPlan.WaitForPreviousServer(
            environment.GetValueOrDefault,
            (name, value) => environment[name] = value,
            _ => true,
            _ => pauses++,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(1)
        );

        gone.Should().BeFalse();
        pauses.Should().Be(60);
        environment[WaitPidVariable].Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-pid")]
    public void WaitForPreviousServer_WithoutAUsablePid_DoesNotWait(string? value)
    {
        Dictionary<string, string?> environment = new() { [WaitPidVariable] = value };
        int probes = 0;

        bool gone = ServerRestartPlan.WaitForPreviousServer(
            environment.GetValueOrDefault,
            (name, variable) => environment[name] = variable,
            _ =>
            {
                probes++;
                return true;
            },
            _ => { },
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(1)
        );

        gone.Should().BeTrue();
        probes.Should().Be(0);
        environment[WaitPidVariable].Should().BeNull();
    }
}
