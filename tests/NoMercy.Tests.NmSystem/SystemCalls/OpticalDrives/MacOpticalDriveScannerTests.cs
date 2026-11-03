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
/// macOS drive listing is parsed from captured <c>diskutil</c> output through
/// <see cref="FakeCommandRunner"/>, so these run on any host. A Mac without a
/// drive (the common case) must produce an empty list, never an exception.
/// </summary>
[Trait("Category", "Unit")]
public class MacOpticalDriveScannerTests
{
    private const string ListingWithDvd = """
        /dev/disk0 (internal, physical):
           #:                       TYPE NAME                    SIZE       IDENTIFIER
           0:      GUID_partition_scheme                        *500.3 GB   disk0
           1:                        EFI EFI                     314.6 MB   disk0s1
           2:                 Apple_APFS Container disk1         500.0 GB   disk0s2

        /dev/disk1 (synthesized):
           #:                       TYPE NAME                    SIZE       IDENTIFIER
           0:      APFS Container Scheme -                      +500.0 GB   disk1
                                         Physical Store disk0s2
           1:                APFS Volume Macintosh HD            15.2 GB    disk1s1
           2:                APFS Volume Data                    120.4 GB   disk1s2

        /dev/disk4 (external, physical):
           #:                       TYPE NAME                    SIZE       IDENTIFIER
           0:        CD_partition_scheme                        *7.9 GB     disk4
           1:                        UDF THE_MOVIE               7.9 GB     disk4s0

        """;

    private const string ListingWithoutOpticalDevice = """
        /dev/disk0 (internal, physical):
           #:                       TYPE NAME                    SIZE       IDENTIFIER
           0:      GUID_partition_scheme                        *500.3 GB   disk0
           1:                        EFI EFI                     314.6 MB   disk0s1
           2:                 Apple_APFS Container disk1         500.0 GB   disk0s2

        """;

    private const string WholeDiskInfo = """
           Device Identifier:         disk4
           Device Node:               /dev/disk4
           Whole:                     Yes
           Part of Whole:             disk4
           Device / Media Name:       HL-DT-ST DVDRW  GX40N Media

           Volume Name:               Not applicable (no file system)
           Mounted:                   Not applicable (no file system)

           Optical Drive Type:        CD-ROM, CD-R, CD-RW, DVD-ROM, DVD-R, DVD-RW
           Optical Media Type:        DVD-ROM
        """;

    private const string VolumeInfo = """
           Device Identifier:         disk4s0
           Device Node:               /dev/disk4s0
           Whole:                     No
           Part of Whole:             disk4

           Volume Name:               THE_MOVIE
           Mounted:                   Yes
           Mount Point:               /Volumes/THE_MOVIE

           File System Personality:   UDF
           Type (Bundle):             udf
        """;

    [Theory]
    [InlineData("")]
    [InlineData("Unknown")]
    public void GetDrives_NoListingOutput_ReturnsNoDrives(string listing)
    {
        // "Unknown" is the sentinel Shell.ExecCommand substitutes for empty
        // output; it used to be treated as a drive path and then indexed.
        FakeCommandRunner runner = new FakeCommandRunner().Respond("diskutil list", listing);
        MacOpticalDriveScanner scanner = new(runner);

        IReadOnlyList<OpticalDriveState> drives = scanner.GetDrives();

        drives
            .Should()
            .BeEmpty("no diskutil output means no optical drive, not a drive called 'Unknown'");
        runner.Calls.Should().ContainSingle(call => call == "diskutil list");
    }

    [Fact]
    public void GetDrives_ListingWithOneDvd_ReturnsThatDriveWithLabelAndMountPoint()
    {
        FakeCommandRunner runner = new FakeCommandRunner()
            .Respond("diskutil list", ListingWithDvd)
            .Respond("diskutil info disk4", WholeDiskInfo)
            .Respond("diskutil info disk4s0", VolumeInfo);
        MacOpticalDriveScanner scanner = new(runner);

        IReadOnlyList<OpticalDriveState> drives = scanner.GetDrives();

        OpticalDriveState drive = drives
            .Should()
            .ContainSingle("the listing has exactly one CD/DVD device")
            .Subject;
        drive.Path.Should().Be("/dev/disk4");
        drive.Label.Should().Be("THE_MOVIE");
        drive.HasDisc.Should().BeTrue();
        drive.MountPoint.Should().Be("/Volumes/THE_MOVIE");
    }

    [Fact]
    public void GetDrives_VolumeInfoWithoutSeparator_ReturnsDriveWithoutLabel()
    {
        FakeCommandRunner runner = new FakeCommandRunner()
            .Respond("diskutil list", ListingWithDvd)
            .Respond("diskutil info disk4", "Unknown")
            .Respond("diskutil info disk4s0", "Unknown");
        MacOpticalDriveScanner scanner = new(runner);

        Func<IReadOnlyList<OpticalDriveState>> act = scanner.GetDrives;

        IReadOnlyList<OpticalDriveState> drives = act.Should()
            .NotThrow("a diskutil info line without ': ' must be skipped, not indexed")
            .Subject;
        OpticalDriveState drive = drives.Should().ContainSingle().Subject;
        drive.Label.Should().BeNull();
        drive.MountPoint.Should().BeNull();
    }

    [Fact]
    public void GetDrives_ListingWithoutOpticalDevice_ReturnsNoDrives()
    {
        FakeCommandRunner runner = new FakeCommandRunner().Respond(
            "diskutil list",
            ListingWithoutOpticalDevice
        );
        MacOpticalDriveScanner scanner = new(runner);

        IReadOnlyList<OpticalDriveState> drives = scanner.GetDrives();

        drives.Should().BeEmpty();
        runner.Calls.Should().ContainSingle("no device means no diskutil info lookups");
    }

    [Fact]
    public void GetDrives_DiskutilMissing_ReturnsNoDrives()
    {
        FakeCommandRunner runner = new FakeCommandRunner().FailWith(
            new Win32Exception("No such file or directory")
        );
        MacOpticalDriveScanner scanner = new(runner);

        IReadOnlyList<OpticalDriveState> drives = scanner.GetDrives();

        drives.Should().BeEmpty();
    }
}
