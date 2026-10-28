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

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// One audio file as a listing or a Track row addresses it. Size and time are null for a
/// row that was never listed in this process.
/// </summary>
public sealed record TrackFileEntry(
    string HostFolder,
    string Filename,
    long? SizeBytes = null,
    DateTimeOffset? LastModified = null
)
{
    public string Path => HostFolder.TrimEnd('/') + Filename;

    // Whole seconds: SMB and NFS round a modified time differently.
    public (long Size, long Seconds)? Identity =>
        SizeBytes is { } size && LastModified is { } time
            ? (size, time.ToUnixTimeSeconds())
            : null;
}
