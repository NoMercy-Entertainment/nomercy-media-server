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

namespace NoMercy.MediaProcessing.Recordings;

/// <summary>Where a track's file now lives, in the shape a Track row stores it.</summary>
public sealed record TrackFilePlacement(
    Ulid FolderId,
    string Folder,
    string HostFolder,
    string Filename
);
