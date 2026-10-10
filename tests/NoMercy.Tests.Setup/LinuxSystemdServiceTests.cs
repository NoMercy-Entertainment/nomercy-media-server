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

using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.Setup;

public class LinuxSystemdServiceTests
{
    [Fact]
    public void Enable_ReloadsUnit_EnablesService_AndEnablesLinger()
    {
        List<(string Executable, string Arguments)> calls = [];
        LinuxSystemdService service = new(
            (executable, arguments) =>
            {
                calls.Add((executable, string.Join(" ", arguments)));
                return Task.FromResult(new Shell.ExecResult { ExitCode = 0 });
            }
        );

        service.Enable("test-user");

        Assert.Equal(
            [
                ("systemctl", "--user daemon-reload"),
                ("systemctl", "--user enable nomercy-mediaserver.service"),
                ("loginctl", "enable-linger test-user"),
            ],
            calls
        );
    }

    [Fact]
    public void Enable_WhenCommandFails_DoesNotReportSuccess()
    {
        LinuxSystemdService service = new(
            (_, _) =>
                Task.FromResult(new Shell.ExecResult { ExitCode = 1, StandardError = "failed" })
        );

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            service.Enable("test-user");
        });
        Assert.Contains("daemon-reload", error.Message);
    }

    [Theory]
    [InlineData(0, "enabled", 0, "yes", true)]
    [InlineData(0, "disabled", 0, "yes", false)]
    [InlineData(1, "", 0, "yes", false)]
    [InlineData(0, "enabled", 0, "no", false)]
    public void IsEnabled_RequiresEnabledUnitAndLinger(
        int unitExitCode,
        string unitOutput,
        int lingerExitCode,
        string lingerOutput,
        bool expected
    )
    {
        LinuxSystemdService service = new(
            (executable, _) =>
                Task.FromResult(
                    executable == "systemctl"
                        ? new Shell.ExecResult
                        {
                            ExitCode = unitExitCode,
                            StandardOutput = unitOutput,
                        }
                        : new Shell.ExecResult
                        {
                            ExitCode = lingerExitCode,
                            StandardOutput = lingerOutput,
                        }
                )
        );

        Assert.Equal(expected, service.IsEnabled("test-user"));
    }

    [Fact]
    public void Disable_DisablesUnitBeforeReload()
    {
        List<string> calls = [];
        LinuxSystemdService service = new(
            (executable, arguments) =>
            {
                calls.Add($"{executable} {string.Join(" ", arguments)}");
                return Task.FromResult(new Shell.ExecResult { ExitCode = 0 });
            }
        );

        service.Disable();

        Assert.Equal(
            [
                "systemctl --user disable nomercy-mediaserver.service",
                "systemctl --user daemon-reload",
            ],
            calls
        );
    }

    [Fact]
    public void HeadlessStatus_IgnoresStaleDesktopEntry()
    {
        LinuxSystemdService service = new(
            (_, _) => Task.FromResult(new Shell.ExecResult { ExitCode = 1 })
        );

        Assert.False(LinuxStartupManager.IsLinuxStartupEnabled(service, false));
    }
}
