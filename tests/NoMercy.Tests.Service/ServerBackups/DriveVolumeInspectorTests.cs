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

using NoMercy.Data.ServerBackups;
using NoMercy.Storage;

namespace NoMercy.Tests.Service.ServerBackups;

[Trait("Category", "Unit")]
public sealed class DriveVolumeInspectorTests
{
    private static readonly string SystemRoot = Path.GetPathRoot(Path.GetTempPath())!;
    private static readonly string OtherDisk = Path.Combine(SystemRoot, "nm-mnt", "disk-b");
    private static readonly string Data = Path.Combine(SystemRoot, "nm-data");

    private readonly Mock<IStorageDriver> _storage = new();

    private DriveVolumeInspector Inspector() => new(_storage.Object, () => [SystemRoot, OtherDisk]);

    private static string Root(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    [Fact]
    public void APath_IsOnTheLongestMountRootThatHoldsIt()
    {
        DriveVolumeInspector inspector = Inspector();

        inspector.VolumeOf(Path.Combine(Data, "media.db")).Should().Be(Root(SystemRoot));
        inspector.VolumeOf(Path.Combine(OtherDisk, "backups", "sets")).Should().Be(Root(OtherDisk));
    }

    [Fact]
    public void AFolderLinkedToAnotherDisk_IsSeenOnThatDisk()
    {
        string linked = Path.Combine(Data, "backups");
        _storage
            .Setup(storage => storage.ResolveLinkTarget(linked))
            .Returns(Path.Combine(OtherDisk, "backups"));

        DriveVolumeInspector inspector = Inspector();

        inspector
            .RealPath(Path.Combine(linked, "sets"))
            .Should()
            .Be(Path.Combine(OtherDisk, "backups", "sets"));
        inspector.VolumeOf(Path.Combine(linked, "sets")).Should().Be(Root(OtherDisk));
        inspector.VolumeOf(Path.Combine(Data, "media.db")).Should().Be(Root(SystemRoot));
    }

    [Fact]
    public void AMountRoot_IsAMountPoint_AndAFolderInsideItIsNot()
    {
        DriveVolumeInspector inspector = Inspector();

        inspector.IsMountPoint(OtherDisk).Should().BeTrue();
        inspector.IsMountPoint(Path.Combine(OtherDisk, "config")).Should().BeFalse();
    }

    [Fact]
    public void ALinkToAMountRoot_IsAMountPoint()
    {
        string config = Path.Combine(Data, "config");
        _storage.Setup(storage => storage.ResolveLinkTarget(config)).Returns(OtherDisk);

        Inspector().IsMountPoint(config).Should().BeTrue();
    }

    [Fact]
    public void WithTheRealMountTable_TheTempFolderHasAVolume()
    {
        DriveVolumeInspector inspector = new(Mock.Of<IStorageDriver>());

        inspector.VolumeOf(Path.GetTempPath()).Should().NotBeNull();
    }
}
