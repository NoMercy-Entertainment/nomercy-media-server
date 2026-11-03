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

namespace NoMercy.OpticalMedia.Drives;

/// <summary>
/// One optical drive as the app sees it. <paramref name="Path"/> identifies the
/// drive (<c>D:\</c>, <c>/dev/sr0</c>, <c>/dev/disk4</c>); <paramref name="MountPoint"/>
/// is where the disc's filesystem is browsable, or null when nothing is mounted.
/// </summary>
public record DiscDrive(
    string Path,
    string? Label,
    bool HasDisc,
    OpticalDiscType DiscType,
    string? MountPoint = null
);
