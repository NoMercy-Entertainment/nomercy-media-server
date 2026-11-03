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

using System.Linq.Expressions;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Database.Models;

/// <summary>
/// The one rule for "has something to play". A show is available when a file
/// belongs to a season 1+ episode (specials alone do not count), a movie when it
/// has a file. Carousel gates and the available/total badge both use it, so the
/// gate and the badge cannot disagree.
/// </summary>
public static class MediaAvailability
{
    public static readonly Expression<Func<Episode, bool>> EpisodeIsAvailable = episode =>
        episode.SeasonNumber > 0 && episode.VideoFiles.Any(file => file.Folder != null);

    public static readonly Expression<Func<Tv, bool>> TvIsAvailable = tv =>
        tv.Episodes.Any(episode =>
            episode.SeasonNumber > 0 && episode.VideoFiles.Any(file => file.Folder != null)
        );

    public static readonly Expression<Func<Movie, bool>> MovieIsAvailable = movie =>
        movie.VideoFiles.Any(file => file.Folder != null);

    private static readonly Func<Episode, bool> EpisodeIsAvailableCompiled =
        EpisodeIsAvailable.Compile();

    // In-memory form of EpisodeIsAvailable, for badge counts over loaded episodes.
    public static bool IsAvailable(this Episode episode)
    {
        return EpisodeIsAvailableCompiled(episode);
    }
}
