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

namespace NoMercy.Data.Repositories;

/// <summary>
/// One watch-progress report for a video file. <see cref="PlaylistId"/> names the
/// collection or special for those playlist types; <see cref="TmdbId"/> names the
/// movie or show for the rest.
/// </summary>
public sealed record WatchProgress(
    Guid UserId,
    string PlaylistType,
    string PlaylistId,
    int TmdbId,
    Ulid VideoFileId,
    int Time,
    string? Audio,
    string? Subtitle,
    string? SubtitleType
);
