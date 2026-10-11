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
using NoMercy.MediaProcessing.Movies;
using NoMercy.Providers.AniList;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Jikan;
using NoMercy.Providers.Jikan.Models;

namespace NoMercy.MediaProcessing.Shows;

public class AnimeEnrichmentService(
    IMediaTypeClassifier mediaTypeClassifier,
    IAniListMetadataProvider aniListMetadataProvider,
    IJikanMetadataProvider jikanMetadataProvider,
    IShowRepository showRepository,
    IMovieRepository movieRepository
) : IAnimeEnrichmentService
{
    // AnimeSeason.Quarter is a non-nullable string; Jikan has no quarter
    // concept, so this sentinel marks a season row built from Jikan's Year
    // alone rather than AniList's Season+SeasonYear pair.
    private const string UnknownQuarter = "UNKNOWN";

    public async Task EnrichTvAsync(
        int tvId,
        string title,
        int? year,
        string[]? originCountry,
        bool? priority = false
    )
    {
        bool hasThemes = await showRepository.HasAnimeThemesAsync(tvId);
        bool hasDemographics = await showRepository.HasAnimeDemographicsAsync(tvId);

        // Recorded themes or demographics only ever come from a successful
        // AniList/Jikan anime match, so they are proof of "anime" that costs no
        // network call. Placement is re-checked from that proof before the
        // already-enriched short-circuit below, because a show enriched by an
        // earlier pass can still be sitting in the library a pre-classifier
        // import (or an inconclusive lookup) filed it under.
        if (hasThemes || hasDemographics)
            await showRepository.EnsureFiledUnderLibraryTypeAsync(tvId, "anime");

        if (hasThemes && hasDemographics)
            return;

        try
        {
            await EnrichTvCoreAsync(tvId, title, year, originCountry, hasThemes, priority);
        }
        catch (Exception)
        {
            // AniList/Jikan are best-effort enrichment, not core import data - a
            // provider outage (mirrors MediaTypeClassifier.IsAnimeAsync's own
            // per-provider try/catch) must never abort ShowManager's import or
            // stall the backfill job's cursor. hasThemes/hasDemographics stay
            // false, so the next pass retries automatically.
        }
    }

    private async Task EnrichTvCoreAsync(
        int tvId,
        string title,
        int? year,
        string[]? originCountry,
        bool hasThemes,
        bool? priority
    )
    {
        // ClassifyAsync itself calls AniList/Jikan (to decide anime vs not),
        // so it belongs behind the same already-enriched short-circuit as the
        // fetches below - a title with recorded themes was, by construction,
        // classified anime on a prior successful run.
        string? classification = await mediaTypeClassifier.ClassifyAsync(
            title,
            year,
            originCountry
        );
        if (classification != "anime")
            return;

        // The classifier just confirmed anime, so a show sitting in any other
        // library is misfiled — whatever put it there (a pre-classifier import,
        // or a lookup that was inconclusive at import time and defaulted to tv).
        await showRepository.EnsureFiledUnderLibraryTypeAsync(tvId, "anime");

        if (!hasThemes)
        {
            (AniListMedia? aniListMatch, JikanAnime? jikanMatch) = await FetchAsync(
                title,
                year,
                priority
            );

            if (aniListMatch is not null)
            {
                List<AnimeThemeTv> themes =
                [
                    .. await ResolveThemesAsync(
                        aniListMatch,
                        name => showRepository.ResolveAnimeThemeIdAsync(name),
                        (id, description) =>
                            showRepository.FillAnimeThemeDescriptionAsync(id, description),
                        animeThemeId => new AnimeThemeTv
                        {
                            AnimeThemeId = animeThemeId,
                            TvId = tvId,
                        }
                    ),
                ];

                if (themes.Count == 0 && jikanMatch is not null)
                    themes =
                    [
                        .. await ResolveThemesAsync(
                            jikanMatch,
                            name => showRepository.ResolveAnimeThemeIdAsync(name),
                            animeThemeId => new AnimeThemeTv
                            {
                                AnimeThemeId = animeThemeId,
                                TvId = tvId,
                            }
                        ),
                    ];

                await showRepository.StoreAnimeThemes(themes);

                if (aniListMatch.SeasonYear is not null && aniListMatch.Season is not null)
                    await showRepository.StoreAnimeSeason(
                        tvId,
                        aniListMatch.SeasonYear.Value,
                        aniListMatch.Season
                    );
            }
            else if (jikanMatch is not null)
            {
                IEnumerable<AnimeThemeTv> themes = await ResolveThemesAsync(
                    jikanMatch,
                    name => showRepository.ResolveAnimeThemeIdAsync(name),
                    animeThemeId => new AnimeThemeTv { AnimeThemeId = animeThemeId, TvId = tvId }
                );
                await showRepository.StoreAnimeThemes(themes);

                if (jikanMatch.Year is not null)
                    await showRepository.StoreAnimeSeason(
                        tvId,
                        jikanMatch.Year.Value,
                        UnknownQuarter
                    );
            }

            await StoreDemographicsIfPresentAsync(
                jikanMatch,
                name => showRepository.ResolveAnimeDemographicIdAsync(name),
                demographics => showRepository.StoreAnimeDemographics(demographics),
                animeDemographicId => new AnimeDemographicTv
                {
                    AnimeDemographicId = animeDemographicId,
                    TvId = tvId,
                }
            );
            return;
        }

        // Themes/season are already stored - AniList's tags/themes have
        // nothing left to give, so only its idMal cross-reference is worth
        // asking for, to route Jikan through the reliable by-id lookup.
        int? malId = await ResolveMalIdAsync(title, year, priority);
        JikanAnime? demographicOnlyMatch = await FetchJikanAsync(title, year, priority, malId);
        await StoreDemographicsIfPresentAsync(
            demographicOnlyMatch,
            name => showRepository.ResolveAnimeDemographicIdAsync(name),
            demographics => showRepository.StoreAnimeDemographics(demographics),
            animeDemographicId => new AnimeDemographicTv
            {
                AnimeDemographicId = animeDemographicId,
                TvId = tvId,
            }
        );
    }

    public async Task EnrichMovieAsync(
        int movieId,
        string title,
        int? year,
        string[]? originCountry,
        bool? priority = false
    )
    {
        bool hasThemes = await movieRepository.HasAnimeThemesAsync(movieId);
        bool hasDemographics = await movieRepository.HasAnimeDemographicsAsync(movieId);
        if (hasThemes && hasDemographics)
            return;

        try
        {
            await EnrichMovieCoreAsync(movieId, title, year, originCountry, hasThemes, priority);
        }
        catch (Exception)
        {
            // See EnrichTvAsync/EnrichTvCoreAsync: a provider outage must never
            // abort MovieManager's import or stall the backfill job's cursor.
        }
    }

    private async Task EnrichMovieCoreAsync(
        int movieId,
        string title,
        int? year,
        string[]? originCountry,
        bool hasThemes,
        bool? priority
    )
    {
        // See EnrichTvAsync: ClassifyAsync itself calls AniList/Jikan, so it
        // sits behind the same already-enriched short-circuit as the fetches
        // below.
        string? classification = await mediaTypeClassifier.ClassifyAsync(
            title,
            year,
            originCountry
        );
        if (classification != "anime")
            return;

        if (!hasThemes)
        {
            (AniListMedia? aniListMatch, JikanAnime? jikanMatch) = await FetchAsync(
                title,
                year,
                priority
            );

            if (aniListMatch is not null)
            {
                List<AnimeThemeMovie> themes =
                [
                    .. await ResolveThemesAsync(
                        aniListMatch,
                        name => movieRepository.ResolveAnimeThemeIdAsync(name),
                        (id, description) =>
                            movieRepository.FillAnimeThemeDescriptionAsync(id, description),
                        animeThemeId => new AnimeThemeMovie
                        {
                            AnimeThemeId = animeThemeId,
                            MovieId = movieId,
                        }
                    ),
                ];

                if (themes.Count == 0 && jikanMatch is not null)
                    themes =
                    [
                        .. await ResolveThemesAsync(
                            jikanMatch,
                            name => movieRepository.ResolveAnimeThemeIdAsync(name),
                            animeThemeId => new AnimeThemeMovie
                            {
                                AnimeThemeId = animeThemeId,
                                MovieId = movieId,
                            }
                        ),
                    ];

                await movieRepository.StoreAnimeThemes(themes);

                if (aniListMatch.SeasonYear is not null && aniListMatch.Season is not null)
                    await movieRepository.StoreAnimeSeason(
                        movieId,
                        aniListMatch.SeasonYear.Value,
                        aniListMatch.Season
                    );
            }
            else if (jikanMatch is not null)
            {
                IEnumerable<AnimeThemeMovie> themes = await ResolveThemesAsync(
                    jikanMatch,
                    name => movieRepository.ResolveAnimeThemeIdAsync(name),
                    animeThemeId => new AnimeThemeMovie
                    {
                        AnimeThemeId = animeThemeId,
                        MovieId = movieId,
                    }
                );
                await movieRepository.StoreAnimeThemes(themes);

                if (jikanMatch.Year is not null)
                    await movieRepository.StoreAnimeSeason(
                        movieId,
                        jikanMatch.Year.Value,
                        UnknownQuarter
                    );
            }

            await StoreDemographicsIfPresentAsync(
                jikanMatch,
                name => movieRepository.ResolveAnimeDemographicIdAsync(name),
                demographics => movieRepository.StoreAnimeDemographics(demographics),
                animeDemographicId => new AnimeDemographicMovie
                {
                    AnimeDemographicId = animeDemographicId,
                    MovieId = movieId,
                }
            );
            return;
        }

        // Themes/season are already stored - AniList's tags/themes have
        // nothing left to give, so only its idMal cross-reference is worth
        // asking for, to route Jikan through the reliable by-id lookup.
        int? malId = await ResolveMalIdAsync(title, year, priority);
        JikanAnime? demographicOnlyMatch = await FetchJikanAsync(title, year, priority, malId);
        await StoreDemographicsIfPresentAsync(
            demographicOnlyMatch,
            name => movieRepository.ResolveAnimeDemographicIdAsync(name),
            demographics => movieRepository.StoreAnimeDemographics(demographics),
            animeDemographicId => new AnimeDemographicMovie
            {
                AnimeDemographicId = animeDemographicId,
                MovieId = movieId,
            }
        );
    }

    // Gated the same way MediaTypeClassifier.IsAnimeAsync gates its own search
    // hits: a provider search is a "best guess" match, not a confirmed one, so
    // every hit must clear TitleMatcher.Matches before it is trusted to write
    // themes/season/demographics against this show or movie.
    private async Task<(AniListMedia?, JikanAnime?)> FetchAsync(
        string title,
        int? year,
        bool? priority
    )
    {
        AniListMedia? aniListMatch = await aniListMetadataProvider.SearchAsync(
            title,
            year,
            priority
        );
        if (
            aniListMatch is not null
            && !TitleMatcher.Matches(
                title,
                [
                    aniListMatch.Title.Romaji,
                    aniListMatch.Title.English,
                    aniListMatch.Title.Native,
                    .. aniListMatch.Synonyms,
                ]
            )
        )
            aniListMatch = null;

        JikanAnime? jikanMatch = await FetchJikanAsync(title, year, priority, aniListMatch?.IdMal);

        return (aniListMatch, jikanMatch);
    }

    // Split out of FetchAsync so a title with themes/season already stored can
    // ask Jikan alone for the still-missing demographic, instead of re-querying
    // AniList for tags it already gave us on a prior successful run.
    //
    // Prefers GetByIdAsync over SearchAsync whenever a MAL id is available:
    // Jikan's /anime search endpoint is a known-unreliable path
    // (jikan-me/jikan-rest#610) that hard-fails independently of MAL's own
    // uptime, while /anime/{id} keeps working - verified live, search 504s on
    // every query while by-id lookups return real data for the same titles.
    private async Task<JikanAnime?> FetchJikanAsync(
        string title,
        int? year,
        bool? priority,
        int? malId = null
    )
    {
        JikanAnime? jikanMatch = malId is not null
            ? await jikanMetadataProvider.GetByIdAsync(malId.Value, priority)
            : await jikanMetadataProvider.SearchAsync(title, year, priority);

        if (
            jikanMatch is not null
            && !TitleMatcher.Matches(title, jikanMatch.Titles.Select(t => t.Title))
        )
            jikanMatch = null;

        return jikanMatch;
    }

    // The demographics-only retry (themes already stored) deliberately makes
    // no further use of AniList's own data - only its idMal cross-reference,
    // so FetchJikanAsync can route through the reliable Jikan by-id lookup
    // instead of the broken search endpoint. This is a narrower ask than the
    // full AniList fetch above: it discards everything except the id.
    private async Task<int?> ResolveMalIdAsync(string title, int? year, bool? priority)
    {
        AniListMedia? aniListMatch = await aniListMetadataProvider.SearchAsync(
            title,
            year,
            priority
        );
        if (
            aniListMatch is null
            || !TitleMatcher.Matches(
                title,
                [
                    aniListMatch.Title.Romaji,
                    aniListMatch.Title.English,
                    aniListMatch.Title.Native,
                    .. aniListMatch.Synonyms,
                ]
            )
        )
            return null;

        return aniListMatch.IdMal;
    }

    // Demographics has no AniList fallback - Jikan is the only source - so this
    // is the single place that decides whether a demographic write is even
    // attempted, shared by the full-fetch path and the demographics-only retry.
    private static async Task StoreDemographicsIfPresentAsync<TLink>(
        JikanAnime? jikanMatch,
        Func<string, Task<int>> resolveDemographicId,
        Func<IEnumerable<TLink>, Task> store,
        Func<int, TLink> toLink
    )
    {
        if (jikanMatch is null || jikanMatch.Demographics.Length == 0)
            return;

        IEnumerable<TLink> demographics = await ResolveDemographicsAsync(
            jikanMatch,
            resolveDemographicId,
            toLink
        );
        await store(demographics);
    }

    // AniList tags carry no stable numeric id (unlike TMDB genres), so each
    // distinct tag name is resolved against the AnimeThemes table by name,
    // creating the row on first sight, before a join row can reference it.
    // Only genuine theme/setting-category tags are treated as themes — AniList
    // also carries Cast-*/Technical/Sexual Content tags that are not themes —
    // and IsAdult is always excluded, mirroring MediaContext's adult-content
    // query filters elsewhere in this codebase.
    private static async Task<IEnumerable<TLink>> ResolveThemesAsync<TLink>(
        AniListMedia aniListMatch,
        Func<string, Task<int>> resolveThemeId,
        Func<int, string, Task> fillThemeDescription,
        Func<int, TLink> toLink
    )
    {
        List<TLink> links = [];
        foreach (AniListMediaTag tag in aniListMatch.Tags.Where(AniListTagFilter.IsTheme))
        {
            int animeThemeId = await resolveThemeId(tag.Name);
            if (!string.IsNullOrWhiteSpace(tag.Description))
                await fillThemeDescription(animeThemeId, tag.Description.Trim());
            links.Add(toLink(animeThemeId));
        }

        return links;
    }

    // Jikan themes carry a MalId that is only stable within MyAnimeList's own
    // numbering, not the local AnimeThemes table, so each distinct theme name
    // is resolved by name (creating the row on first sight) before a join row
    // can reference it — used only as a fallback when AniList returns no
    // theme-category tags for a title, mirroring ResolveThemesAsync above.
    private static async Task<IEnumerable<TLink>> ResolveThemesAsync<TLink>(
        JikanAnime jikanMatch,
        Func<string, Task<int>> resolveThemeId,
        Func<int, TLink> toLink
    )
    {
        List<TLink> links = [];
        foreach (
            JikanGenre theme in jikanMatch.Themes.Where(t => !string.IsNullOrWhiteSpace(t.Name))
        )
        {
            int animeThemeId = await resolveThemeId(theme.Name);
            links.Add(toLink(animeThemeId));
        }

        return links;
    }

    // Jikan demographics carry a MalId that is only stable within MyAnimeList's
    // own numbering, not the local AnimeDemographics table, so each distinct
    // demographic name is resolved by name (creating the row on first sight)
    // before a join row can reference it — mirrors ResolveThemesAsync above.
    private static async Task<IEnumerable<TLink>> ResolveDemographicsAsync<TLink>(
        JikanAnime jikanMatch,
        Func<string, Task<int>> resolveDemographicId,
        Func<int, TLink> toLink
    )
    {
        List<TLink> links = [];
        foreach (
            JikanGenre demographic in jikanMatch.Demographics.Where(d =>
                !string.IsNullOrWhiteSpace(d.Name)
            )
        )
        {
            int animeDemographicId = await resolveDemographicId(demographic.Name);
            links.Add(toLink(animeDemographicId));
        }

        return links;
    }
}
