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

namespace NoMercy.Data.Repositories;

public interface IContentSegmentRepository
{
    Task<List<ContentSegment>> GetForEpisodeAsync(int episodeId);

    Task<List<ContentSegment>> ListAsync(
        int pageSize,
        int pageIndex,
        ContentSegmentType? filterType = null
    );

    Task<int> GetTotalCountAsync();

    Task<List<ContentSegment>> GetForMovieAsync(int movieId);

    Task<ContentSegment?> GetByIdAsync(Ulid id);

    Task<ContentSegment> CreateAsync(ContentSegment segment);

    Task<ContentSegment?> UpdateAsync(Ulid id, Action<ContentSegment> apply);

    Task<bool> DeleteAsync(Ulid id);

    Task ReplaceDetectorSegmentsForEpisodeAsync(
        int episodeId,
        IReadOnlyList<ContentSegment> newSegments
    );

    /// <summary>
    /// Bulk read across many episodes filtered by <see cref="ContentSegment.Source"/>.
    /// Used by season-wide detection to find existing rows (e.g. manual
    /// overrides to skip, or stale auto rows to replace) in one query
    /// instead of one round trip per episode.
    /// </summary>
    Task<List<ContentSegment>> GetForEpisodesBySourceAsync(
        IReadOnlyList<int> episodeIds,
        string source
    );

    /// <summary>
    /// Removes every row matching <paramref name="staleSource"/> across
    /// <paramref name="episodeIds"/> and inserts <paramref name="newSegments"/>,
    /// in one SaveChanges. Rows with a different source (e.g. manual edits)
    /// are left untouched.
    /// </summary>
    Task ReplaceSegmentsForEpisodesAsync(
        IReadOnlyList<int> episodeIds,
        string staleSource,
        IReadOnlyList<ContentSegment> newSegments
    );
}
