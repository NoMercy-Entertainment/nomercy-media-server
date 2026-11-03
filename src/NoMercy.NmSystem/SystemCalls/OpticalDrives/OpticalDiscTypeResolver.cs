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

using NoMercy.NmSystem.Dto;
using NoMercy.Storage;

namespace NoMercy.NmSystem.SystemCalls.OpticalDrives;

/// <summary>
/// Classifies an inserted disc by the folders at its mount point: BDMV is a
/// Blu-ray, VIDEO_TS a DVD, anything else that mounted is a CD. The device
/// node (<c>/dev/sr0</c>, <c>/dev/disk4</c>) is never a directory, so only the
/// mount point is inspected; on Windows the two are the same drive root.
/// </summary>
public sealed class OpticalDiscTypeResolver(IStorageDriver storageDriver)
{
    private const string BluRayFolder = "BDMV";
    private const string DvdFolder = "VIDEO_TS";

    public OpticalDiscType Resolve(OpticalDriveState drive)
    {
        if (!drive.HasDisc || string.IsNullOrEmpty(drive.MountPoint))
            return OpticalDiscType.None;

        if (!storageDriver.DirectoryExists(drive.MountPoint))
            return OpticalDiscType.None;

        if (storageDriver.DirectoryExists(ChildPath(drive.MountPoint, BluRayFolder)))
            return OpticalDiscType.BluRay;

        if (storageDriver.DirectoryExists(ChildPath(drive.MountPoint, DvdFolder)))
            return OpticalDiscType.Dvd;

        return OpticalDiscType.Cd;
    }

    /// <summary>
    /// Joins with the separator the mount point already uses, so a Linux mount
    /// stays forward-slashed whichever host evaluates it.
    /// </summary>
    private static string ChildPath(string mountPoint, string folder)
    {
        char separator = mountPoint.Contains('\\') ? '\\' : '/';
        return mountPoint.TrimEnd('\\', '/') + separator + folder;
    }
}
