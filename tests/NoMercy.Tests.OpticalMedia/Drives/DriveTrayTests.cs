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

using Moq;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls.OpticalDrives;
using NoMercy.OpticalMedia.Drives;

namespace NoMercy.Tests.OpticalMedia.Drives;

/// <summary>
/// The open/close route takes a drive path from the URL. Only a drive the
/// monitor reports may reach eject/drutil; anything else is refused before a
/// tool runs, and the tool's own failure is reported as failure.
/// </summary>
[Trait("Category", "Unit")]
public class DriveTrayTests
{
    private static (DriveTray Tray, Mock<ITrayCommands> Commands) TrayWith(
        bool commandResult,
        params DiscDrive[] knownDrives
    )
    {
        Mock<IDriveMonitor> monitor = new();
        monitor.Setup(m => m.GetDrives()).Returns(knownDrives);
        Mock<ITrayCommands> commands = new();
        commands.Setup(c => c.Open(It.IsAny<string>())).Returns(commandResult);
        commands.Setup(c => c.Close(It.IsAny<string>())).Returns(commandResult);
        return (new DriveTray(monitor.Object, commands.Object), commands);
    }

    private static DiscDrive LinuxDrive() =>
        new("/dev/sr0", "MOVIE", HasDisc: true, OpticalDiscType.Dvd, MountPoint: "/media/MOVIE");

    [Theory]
    [InlineData("/dev/sda")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void Open_UnknownDrivePath_IsRefusedWithoutRunningTheTool(string drivePath)
    {
        (DriveTray tray, Mock<ITrayCommands> commands) = TrayWith(true, LinuxDrive());

        bool opened = tray.Open(drivePath);

        opened.Should().BeFalse("only a drive the monitor reports may be ejected");
        commands.Verify(c => c.Open(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Close_UnknownDrivePath_IsRefusedWithoutRunningTheTool()
    {
        (DriveTray tray, Mock<ITrayCommands> commands) = TrayWith(true, LinuxDrive());

        bool closed = tray.Close("/dev/sda");

        closed.Should().BeFalse();
        commands.Verify(c => c.Close(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Open_KnownDriveButToolFails_ReturnsFalse()
    {
        (DriveTray tray, _) = TrayWith(false, LinuxDrive());

        bool opened = tray.Open("/dev/sr0");

        opened.Should().BeFalse("the eject tool reported failure");
    }

    [Fact]
    public void Open_KnownWindowsDriveWithoutTrailingSeparator_UsesTheMonitorPath()
    {
        DiscDrive windowsDrive = new("D:\\", null, HasDisc: true, OpticalDiscType.Cd, "D:\\");
        (DriveTray tray, Mock<ITrayCommands> commands) = TrayWith(true, windowsDrive);

        bool opened = tray.Open("d:");

        opened.Should().BeTrue();
        commands.Verify(c => c.Open("D:\\"), Times.Once);
    }

    [Fact]
    public void Close_KnownDrive_PassesTheMonitorPathToTheTool()
    {
        (DriveTray tray, Mock<ITrayCommands> commands) = TrayWith(true, LinuxDrive());

        bool closed = tray.Close("/dev/sr0");

        closed.Should().BeTrue();
        commands.Verify(c => c.Close("/dev/sr0"), Times.Once);
    }
}
