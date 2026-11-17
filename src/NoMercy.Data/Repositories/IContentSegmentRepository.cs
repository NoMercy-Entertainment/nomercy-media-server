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
using NoMercy.Database.Segments;

namespace NoMercy.Data.Repositories;

public interface IContentSegmentRepository
{
    Task<List<ContentSegment>> GetForEpisodeAsync(int episodeId);

    Task<List<ContentSegment>> ListAsync(
        int pageSize,
        int pageIndex,
        ContentSegmentType? filterType = null
    );

    Task<int> GetTotalCountAsync(ContentSegmentType? filterType = null);

    Task<List<ContentSegment>> GetForMovieAsync(int movieId);

    Task<ContentSegment?> GetByIdAsync(Ulid id);

    Task<ContentSegment> CreateAsync(ContentSegment segment);

    Task<ContentSegment?> UpdateAsync(Ulid id, Action<ContentSegment> apply);

    Task<bool> DeleteAsync(Ulid id);

    /// <summary>Every row, of any source, of the given episodes in one query.</summary>
    Task<List<ContentSegment>> GetForEpisodesAsync(IReadOnlyList<int> episodeIds);

    /// <summary>
    /// Applies precedence plans (see <see cref="ContentSegmentPrecedence"/>) in
    /// one SaveChanges.
    /// </summary>
    Task ApplyAsync(IReadOnlyList<ContentSegmentWritePlan> plans);
}
