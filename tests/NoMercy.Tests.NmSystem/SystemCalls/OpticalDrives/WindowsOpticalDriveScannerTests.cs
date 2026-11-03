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

namespace NoMercy.Tests.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Whether a Windows drive holds a disc comes from <c>DriveInfo.IsReady</c>,
/// not from the volume label: many burned and some pressed discs carry no
/// label, and mapping "no label" to "no disc" hid them completely.
/// </summary>
[Trait("Category", "Unit")]
public class WindowsOpticalDriveScannerTests
{
    private static WindowsOpticalDriveScanner ScannerFor(params CdRomDriveSnapshot[] drives)
    {
        Mock<ICdRomDriveSource> source = new();
        source.Setup(s => s.GetCdRomDrives()).Returns(drives);
        return new(source.Object);
    }

    [Fact]
    public void GetDrives_ReadyDriveWithEmptyLabel_HasDisc()
    {
        WindowsOpticalDriveScanner scanner = ScannerFor(
            new CdRomDriveSnapshot("E:\\", IsReady: true, VolumeLabel: "")
        );

        OpticalDriveState drive = scanner.GetDrives().Should().ContainSingle().Subject;

        drive.HasDisc.Should().BeTrue("a ready drive holds a disc even when it has no label");
        drive.Label.Should().BeNull();
        drive.MountPoint.Should().Be("E:\\");
    }

    [Fact]
    public void GetDrives_ReadyDriveWithLabel_HasDiscAndLabel()
    {
        WindowsOpticalDriveScanner scanner = ScannerFor(
            new CdRomDriveSnapshot("D:\\", IsReady: true, VolumeLabel: "THE_MOVIE")
        );

        OpticalDriveState drive = scanner.GetDrives().Should().ContainSingle().Subject;

        drive.HasDisc.Should().BeTrue();
        drive.Label.Should().Be("THE_MOVIE");
        drive.MountPoint.Should().Be("D:\\");
    }

    [Fact]
    public void GetDrives_NotReadyDrive_HasNoDisc()
    {
        WindowsOpticalDriveScanner scanner = ScannerFor(
            new CdRomDriveSnapshot("D:\\", IsReady: false, VolumeLabel: "")
        );

        OpticalDriveState drive = scanner.GetDrives().Should().ContainSingle().Subject;

        drive.HasDisc.Should().BeFalse("a drive that is not ready has no disc");
        drive.Label.Should().BeNull();
        drive.MountPoint.Should().BeNull();
    }

    [Fact]
    public void GetDrives_NoCdRomDrives_ReturnsNoDrives()
    {
        WindowsOpticalDriveScanner scanner = ScannerFor();

        scanner.GetDrives().Should().BeEmpty();
    }
}
