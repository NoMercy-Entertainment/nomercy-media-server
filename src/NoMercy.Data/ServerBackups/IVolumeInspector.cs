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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// Which mounted volume a path lives on. A restore swaps files by renaming them,
/// and a rename is only atomic, and only keeps every byte in place, within one
/// volume.
/// </summary>
public interface IVolumeInspector
{
    /// <summary>The root of the volume holding <paramref name="path"/>; null when unknown.</summary>
    string? VolumeOf(string path);

    /// <summary>True when <paramref name="path"/> is itself the root of a mounted volume.</summary>
    bool IsMountPoint(string path);

    /// <summary>
    /// The bytes still free for this server on the volume holding
    /// <paramref name="path"/>; null when that cannot be told.
    /// </summary>
    long? FreeBytesAt(string path) => null;
}
