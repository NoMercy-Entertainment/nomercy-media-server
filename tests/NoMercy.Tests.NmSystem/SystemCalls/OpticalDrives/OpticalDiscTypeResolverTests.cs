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
using NoMercy.Storage;

namespace NoMercy.Tests.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// The disc type is read from the folder where the disc is mounted, not from
/// the device node: on Linux the drive is /dev/sr0 and on macOS /dev/disk4,
/// neither of which is a directory, so testing the device path for BDMV or
/// VIDEO_TS always yielded None.
/// </summary>
[Trait("Category", "Unit")]
public class OpticalDiscTypeResolverTests
{
    private const string DevicePath = "/dev/sr0";
    private const string MountPoint = "/media/phil/MOVIE";

    private static OpticalDriveState Drive(string? mountPoint, bool hasDisc = true) =>
        new(Path: DevicePath, Label: "MOVIE", HasDisc: hasDisc, MountPoint: mountPoint);

    private static (OpticalDiscTypeResolver Resolver, Mock<IStorageDriver> Storage) ResolverWith(
        params string[] existingDirectories
    )
    {
        HashSet<string> existing = new(existingDirectories, StringComparer.Ordinal);
        Mock<IStorageDriver> storage = new();
        storage
            .Setup(s => s.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) => existing.Contains(path));
        return (new OpticalDiscTypeResolver(storage.Object), storage);
    }

    [Fact]
    public void Resolve_MountPointWithBdmv_IsBluRay()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith(MountPoint, $"{MountPoint}/BDMV");

        OpticalDiscType type = resolver.Resolve(Drive(MountPoint));

        type.Should()
            .Be(OpticalDiscType.BluRay, "BDMV is looked up under the mount point, not /dev/sr0");
    }

    [Fact]
    public void Resolve_MountPointWithVideoTs_IsDvd()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith(MountPoint, $"{MountPoint}/VIDEO_TS");

        OpticalDiscType type = resolver.Resolve(Drive(MountPoint));

        type.Should().Be(OpticalDiscType.Dvd);
    }

    [Fact]
    public void Resolve_MountedDiscWithoutVideoStructure_IsCd()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith(MountPoint);

        OpticalDiscType type = resolver.Resolve(Drive(MountPoint));

        type.Should()
            .Be(OpticalDiscType.Cd, "a mounted disc that is neither Blu-ray nor DVD is a CD");
    }

    [Fact]
    public void Resolve_NoMountPoint_IsNoneWithoutTouchingStorage()
    {
        (OpticalDiscTypeResolver resolver, Mock<IStorageDriver> storage) = ResolverWith(MountPoint);

        OpticalDiscType type = resolver.Resolve(Drive(mountPoint: null));

        type.Should().Be(OpticalDiscType.None, "an unmounted disc has no browsable structure");
        storage.Verify(s => s.DirectoryExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Resolve_MountPointMissingOnDisk_IsNone()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith();

        OpticalDiscType type = resolver.Resolve(Drive(MountPoint));

        type.Should().Be(OpticalDiscType.None);
    }

    [Fact]
    public void Resolve_EmptyDrive_IsNone()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith(MountPoint, $"{MountPoint}/BDMV");

        OpticalDiscType type = resolver.Resolve(Drive(MountPoint, hasDisc: false));

        type.Should().Be(OpticalDiscType.None);
    }

    [Fact]
    public void Resolve_WindowsDriveRoot_JoinsWithBackslash()
    {
        (OpticalDiscTypeResolver resolver, _) = ResolverWith("D:\\", "D:\\BDMV");

        OpticalDiscType type = resolver.Resolve(
            new(Path: "D:\\", Label: null, HasDisc: true, MountPoint: "D:\\")
        );

        type.Should().Be(OpticalDiscType.BluRay);
    }
}
