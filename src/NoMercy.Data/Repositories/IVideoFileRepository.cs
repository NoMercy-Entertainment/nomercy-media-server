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

using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public interface IVideoFileRepository
{
    Task<VideoFile?> GetByIdAsync(Ulid id, CancellationToken ct = default);

    Task<bool> ExistsAsync(Ulid id, CancellationToken ct = default);

    /// <summary>
    /// The file with its metadata, when the user can reach it through the movie or the
    /// episode it belongs to; null otherwise.
    /// </summary>
    Task<VideoFile?> GetForUserWithMetadataAsync(
        Ulid id,
        Guid userId,
        CancellationToken ct = default
    );

    /// <summary>Whether <paramref name="folderId"/> is a library folder row.</summary>
    Task<bool> IsLibraryFolderAsync(Ulid folderId, CancellationToken ct = default);

    /// <summary>Whether a video file is stored at this forward-slash host path.</summary>
    Task<bool> ExistsAtHostPathAsync(string hostPath, CancellationToken ct = default);

    Task<List<Episode>> GetEncodedEpisodesForSeasonAsync(
        int seasonId,
        CancellationToken ct = default
    );

    /// <summary>Looks up the parent show id for a season, for response shaping.</summary>
    Task<int> GetShowIdForSeasonAsync(int seasonId, CancellationToken ct = default);
}
