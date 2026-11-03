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

using System.ComponentModel;
using NoMercy.NmSystem.SystemCalls.OpticalDrives;

namespace NoMercy.Tests.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Opening or closing a tray on Linux/macOS reports what eject/drutil actually
/// did: the old code ignored the exit code and returned true unless the call
/// threw, so the dashboard said "Drive opened." when nothing happened.
/// </summary>
[Trait("Category", "Unit")]
public class UnixTrayCommandsTests
{
    [Theory]
    [InlineData(false, "eject /dev/sr0")]
    [InlineData(true, "drutil eject /dev/disk4")]
    public void Open_ToolSucceeds_ReturnsTrueAndPassesDeviceAsOneArgument(
        bool isMac,
        string expectedCommand
    )
    {
        string device = isMac ? "/dev/disk4" : "/dev/sr0";
        FakeCommandRunner runner = new FakeCommandRunner().Respond(
            expectedCommand,
            "",
            exitCode: 0
        );

        bool opened = new UnixTrayCommands(runner, isMac).Open(device);

        opened.Should().BeTrue();
        runner.Calls.Should().Equal(expectedCommand);
    }

    [Theory]
    [InlineData(false, "eject /dev/sr0")]
    [InlineData(true, "drutil eject /dev/sr0")]
    public void Open_ToolExitsNonZero_ReturnsFalse(bool isMac, string command)
    {
        FakeCommandRunner runner = new FakeCommandRunner().Respond(command, "", exitCode: 1);

        bool opened = new UnixTrayCommands(runner, isMac).Open("/dev/sr0");

        opened.Should().BeFalse("eject exited with code 1, so the tray did not open");
    }

    [Theory]
    [InlineData(false, "eject -t /dev/sr0")]
    [InlineData(true, "drutil tray close /dev/sr0")]
    public void Close_ToolExitsNonZero_ReturnsFalse(bool isMac, string command)
    {
        FakeCommandRunner runner = new FakeCommandRunner().Respond(command, "", exitCode: 1);

        bool closed = new UnixTrayCommands(runner, isMac).Close("/dev/sr0");

        closed.Should().BeFalse("the close command exited with code 1");
        runner.Calls.Should().Equal(command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenAndClose_ToolMissing_ReturnFalse(bool isMac)
    {
        FakeCommandRunner runner = new FakeCommandRunner().FailWith(
            new Win32Exception("No such file or directory")
        );
        UnixTrayCommands commands = new(runner, isMac);

        bool opened = commands.Open("/dev/sr0");
        bool closed = commands.Close("/dev/sr0");

        opened.Should().BeFalse("a tool that cannot be started did not open anything");
        closed.Should().BeFalse();
    }
}
