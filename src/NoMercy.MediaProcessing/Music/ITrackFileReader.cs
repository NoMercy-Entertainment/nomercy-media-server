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

using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>What an audio file's own tags say about it.</summary>
public sealed record TrackFileFacts(int TrackNumber, TimeSpan Duration);

/// <summary>Reads the track number and length of an audio file; null when it cannot be read.</summary>
public interface ITrackFileReader
{
    Task<TrackFileFacts?> ReadAsync(
        IStorageDriver driver,
        string path,
        CancellationToken cancellationToken
    );
}
