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
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.MediaProcessing.Shows;

public class MediaTypeClassifier(
    IAniListMetadataProvider aniListMetadataProvider,
    IJikanMetadataProvider jikanMetadataProvider
) : IMediaTypeClassifier
{
    public Task<string?> ClassifyAsync(TmdbTvShowAppends show)
    {
        return ClassifyAsync(show.Name, show.FirstAirDate.ParseYear(), show.OriginCountry);
    }

    public async Task<string?> ClassifyAsync(string name, int? year, string[]? originCountry = null)
    {
        bool? isAnime = await IsAnimeAsync(name, year ?? 0);

        // Same safety rule the Kitsu-era classifier used: a title match alone
        // is not enough. Origin check uses TMDB's origin_country only — Jikan
        // has no reliable country field. An empty list is unknown, like null:
        // only a stated non-Japanese origin vetoes the match.
        if (
            isAnime == true
            && originCountry is { Length: > 0 }
            && !originCountry.Any(c => string.Equals(c, "JP", StringComparison.OrdinalIgnoreCase))
        )
            isAnime = false;

        return isAnime switch
        {
            true => "anime",
            false => "tv",
            null => null,
        };
    }

    private async Task<bool?> IsAnimeAsync(string title, int year)
    {
        bool aniListErrored = false;
        AniListMedia? aniListMatch = null;
        try
        {
            aniListMatch = await aniListMetadataProvider.SearchAsync(
                title,
                year == 0 ? null : year
            );
        }
        catch (Exception)
        {
            aniListErrored = true;
        }

        if (aniListMatch is not null)
        {
            bool matched = TitleMatcher.Matches(
                title,
                [
                    aniListMatch.Title.Romaji,
                    aniListMatch.Title.English,
                    aniListMatch.Title.Native,
                    .. aniListMatch.Synonyms,
                ]
            );
            if (matched)
                return true;
        }

        bool jikanErrored = false;
        JikanAnime? jikanMatch = null;
        try
        {
            jikanMatch = await jikanMetadataProvider.SearchAsync(title, year == 0 ? null : year);
        }
        catch (Exception)
        {
            jikanErrored = true;
        }

        if (jikanMatch is not null)
        {
            bool matched = TitleMatcher.Matches(title, jikanMatch.Titles.Select(t => t.Title));
            if (matched)
                return true;
        }

        // A network/parse failure on both providers is "we don't know",
        // never collapsed to false — a transient failure must not evict an
        // already-correctly-filed show from its library.
        if (aniListErrored && jikanErrored)
            return null;

        return false;
    }
}
