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
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls.OpticalDrives;

namespace NoMercy.Tests.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Linux drive listing is parsed from captured <c>lsblk -P</c> key="value"
/// output through <see cref="FakeCommandRunner"/>. The old columnar parse
/// shifted the label into the mount-point column whenever a cell was empty,
/// and <c>grep sr</c> matched any mount path containing "sr" such as /srv.
/// </summary>
[Trait("Category", "Unit")]
public class LinuxOpticalDriveScannerTests
{
    private const string LsblkCommand = "lsblk -P -o NAME,TYPE,MOUNTPOINT,LABEL,FSTYPE";

    private const string UnmountedDiscWithLabel = """
        NAME="sda" TYPE="disk" MOUNTPOINT="" LABEL="" FSTYPE=""
        NAME="sda1" TYPE="part" MOUNTPOINT="/boot/efi" LABEL="" FSTYPE="vfat"
        NAME="sda2" TYPE="part" MOUNTPOINT="/" LABEL="" FSTYPE="ext4"
        NAME="sr0" TYPE="rom" MOUNTPOINT="" LABEL="MY_MOVIE" FSTYPE="udf"

        """;

    private const string SrvMountWithoutOpticalDrive = """
        NAME="sda" TYPE="disk" MOUNTPOINT="" LABEL="" FSTYPE=""
        NAME="sda1" TYPE="part" MOUNTPOINT="/" LABEL="" FSTYPE="ext4"
        NAME="sdb" TYPE="disk" MOUNTPOINT="" LABEL="" FSTYPE=""
        NAME="sdb1" TYPE="part" MOUNTPOINT="/srv" LABEL="srv-data" FSTYPE="ext4"

        """;

    private const string EmptyTray = """
        NAME="sda" TYPE="disk" MOUNTPOINT="" LABEL="" FSTYPE=""
        NAME="sr0" TYPE="rom" MOUNTPOINT="" LABEL="" FSTYPE=""

        """;

    private const string MountedDiscWithSpacesAndEscapes = """
        NAME="sr0" TYPE="rom" MOUNTPOINT="/media/phil/MY MOVIE" LABEL="MY \x22MOVIE\x22" FSTYPE="udf"

        """;

    private const string UnlabelledDataDisc = """
        NAME="sr0" TYPE="rom" MOUNTPOINT="" LABEL="" FSTYPE="iso9660"

        """;

    private static LinuxOpticalDriveScanner ScannerFor(string lsblkOutput, int exitCode = 0) =>
        new(new FakeCommandRunner().Respond(LsblkCommand, lsblkOutput, exitCode));

    [Fact]
    public void GetDrives_UnmountedDiscWithLabel_ReportsLabelAndNoMountPoint()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(UnmountedDiscWithLabel).GetDrives();

        OpticalDriveState drive = drives.Should().ContainSingle().Subject;
        drive.Path.Should().Be("/dev/sr0");
        drive
            .Label.Should()
            .Be(
                "MY_MOVIE",
                "an empty MOUNTPOINT cell must not shift the label into another column"
            );
        drive.HasDisc.Should().BeTrue();
        drive.MountPoint.Should().BeNull();
    }

    [Fact]
    public void GetDrives_SrvMountWithoutOpticalDrive_ReturnsNoDrives()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(SrvMountWithoutOpticalDrive)
            .GetDrives();

        drives.Should().BeEmpty("/srv is a partition mount, not a rom device");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void GetDrives_NoBlockDevices_ReturnsNoDrives(string lsblkOutput)
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(lsblkOutput).GetDrives();

        drives.Should().BeEmpty();
    }

    [Fact]
    public void GetDrives_EmptyTray_ReportsDriveWithoutDisc()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(EmptyTray).GetDrives();

        OpticalDriveState drive = drives.Should().ContainSingle().Subject;
        drive.Path.Should().Be("/dev/sr0");
        drive.HasDisc.Should().BeFalse("a rom device with no filesystem, label or mount is empty");
        drive.Label.Should().BeNull();
        drive.MountPoint.Should().BeNull();
    }

    [Fact]
    public void GetDrives_MountedDisc_KeepsSpacesAndDecodesEscapes()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(MountedDiscWithSpacesAndEscapes)
            .GetDrives();

        OpticalDriveState drive = drives.Should().ContainSingle().Subject;
        drive.MountPoint.Should().Be("/media/phil/MY MOVIE");
        drive.Label.Should().Be("MY \"MOVIE\"", "lsblk -P encodes a double quote as \\x22");
        drive.HasDisc.Should().BeTrue();
    }

    [Fact]
    public void GetDrives_UnlabelledDataDisc_HasDiscWithoutLabel()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor(UnlabelledDataDisc).GetDrives();

        OpticalDriveState drive = drives.Should().ContainSingle().Subject;
        drive
            .HasDisc.Should()
            .BeTrue("a filesystem type means a disc is present even without a label");
        drive.Label.Should().BeNull();
    }

    [Fact]
    public void GetDrives_LsblkFails_ReturnsNoDrives()
    {
        IReadOnlyList<OpticalDriveState> drives = ScannerFor("lsblk: failed", exitCode: 1)
            .GetDrives();

        drives.Should().BeEmpty();
    }

    [Fact]
    public void GetDrives_LsblkMissing_ReturnsNoDrives()
    {
        FakeCommandRunner runner = new FakeCommandRunner().FailWith(
            new Win32Exception("No such file or directory")
        );
        LinuxOpticalDriveScanner scanner = new(runner);

        IReadOnlyList<OpticalDriveState> drives = scanner.GetDrives();

        drives.Should().BeEmpty();
    }
}
