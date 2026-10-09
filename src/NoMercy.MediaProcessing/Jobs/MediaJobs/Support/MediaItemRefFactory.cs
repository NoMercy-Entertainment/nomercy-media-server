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

using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Naming;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs.Support;

/// <summary>
/// Builds the <see cref="MediaItemRef"/> an encode request carries to identify
/// what it is encoding. Every job that opens an <c>EncodingRequest</c> needs one:
/// a request without it plans a null <c>BundleLayout</c>, and FinalizeStage then
/// writes neither manifest.json nor reconstruction.json — the encode completes
/// with no record of how to reproduce or revert it.
/// </summary>
public static class MediaItemRefFactory
{
    public static MediaItemRef Create(Movie? movie, Episode? episode)
    {
        if (movie is not null)
            return new MovieMediaRef(
                Type: MediaType.Movie,
                Id: movie.Id,
                Title: movie.Title,
                Year: movie.ReleaseDate?.Year,
                Description: movie.Overview
            );

        return new EpisodeMediaRef(
            Type: MediaType.Episode,
            Id: episode!.Id,
            Title: episode.Title ?? episode.CreateTitle(),
            Year: episode.AirDate?.Year,
            ShowTitle: episode.Tv.Title,
            SeasonNumber: episode.SeasonNumber,
            EpisodeNumber: episode.EpisodeNumber,
            // Episode.TvId is the show's own TMDB id (Tv.Id uses the same
            // DatabaseGeneratedOption.None-from-TMDB convention as Episode.Id
            // and Movie.Id), so no extra navigation load is needed here.
            ShowTmdbId: episode.TvId,
            Description: episode.Overview
        );
    }
}
