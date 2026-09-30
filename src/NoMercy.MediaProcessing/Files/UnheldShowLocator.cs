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

using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Which TMDB show and episode a file in an Add content selection belongs to, when the
/// server holds no rows for it yet. The add request carries only an episode id and a
/// path, and an episode id does not say which show it is from, so the show is found the
/// way the listing found it: from the name.
/// </summary>
public static class UnheldShowLocator
{
    public static async Task<(int ShowId, int Season, int Episode)?> LocateAsync(
        IFilenameParserPipeline pipeline,
        string inputFile,
        string libraryType
    )
    {
        string fileName = Path.GetFileName(inputFile);
        string? directory = Path.GetDirectoryName(inputFile);

        ResolvedName resolved = new FilenameResolver(pipeline).Resolve(
            fileName,
            directory,
            inputFile,
            libraryType
        );

        if (resolved.Parsed.Title == null || !resolved.Parsed.Episode.HasValue)
            return null;

        int showId;
        if (resolved.OverrideTmdbId.HasValue)
        {
            showId = resolved.OverrideTmdbId.Value;
        }
        else
        {
            TmdbPaginatedResponse<TmdbTvShow>? shows = await new TmdbSearchClient().TvShow(
                resolved.Parsed.Title.OrEmpty(),
                resolved.Parsed.Year.OrEmpty(),
                true
            );
            TmdbTvShow? first = shows?.Results.FirstOrDefault();
            if (first == null)
                return null;
            showId = first.Id;
        }

        return (showId, resolved.Parsed.Season ?? 1, resolved.Parsed.Episode.Value);
    }
}
