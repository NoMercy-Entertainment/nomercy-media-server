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

public class AnimeDemographicWithCountsDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int TotalMovies { get; set; }
    public int TotalTvShows { get; set; }
    public int MoviesWithVideo { get; set; }
    public int TvShowsWithVideo { get; set; }

    // Posters of the titles inside, for the card of a group with no image of its own.
    public GroupPoster[] ItemPosters { get; set; } = [];
}
