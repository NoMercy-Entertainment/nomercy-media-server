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
namespace NoMercy.MediaProcessing.Trailers;

/// <summary>
/// YouTube trailers fetched with yt-dlp into the transcode folder, one folder per
/// video id. Callers must pass an id that passed <see cref="TrailerCache.IsValidId"/>.
/// </summary>
public interface ITrailerCache
{
    /// <summary>
    /// True when the trailer's info is stored, fetching it first when it is not.
    /// False when yt-dlp cannot describe the video.
    /// </summary>
    Task<bool> FetchInfoAsync(string trailerId, CancellationToken ct = default);

    Task<TrailerInfo?> ReadInfoAsync(string trailerId, CancellationToken ct = default);

    /// <summary>
    /// Starts the download when no segment exists yet and waits up to 30 seconds
    /// for the first one to land.
    /// </summary>
    Task EnsureSegmentsAsync(string trailerId, string language, CancellationToken ct = default);

    /// <summary>False when the trailer folder exists but cannot be deleted.</summary>
    Task<bool> RemoveAsync(string trailerId, CancellationToken ct = default);
}
