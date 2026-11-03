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

namespace NoMercy.NmSystem.Dto;

/// <summary>
/// Snapshot of one optical drive as the OS reports it.
/// <paramref name="Path"/> is the drive's identity (<c>D:\</c>, <c>/dev/sr0</c>,
/// <c>/dev/disk4</c>); <paramref name="MountPoint"/> is where the disc's
/// filesystem can be browsed, or null when nothing is mounted. The two only
/// coincide on Windows.
/// </summary>
public sealed record OpticalDriveState(
    string Path,
    string? Label,
    bool HasDisc,
    string? MountPoint
);
