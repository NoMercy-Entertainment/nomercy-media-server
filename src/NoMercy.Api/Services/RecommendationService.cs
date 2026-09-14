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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.Recommendations;
using NoMercy.Database;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Api.Services;

public class RecommendationService
{
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly IRecommendationRepository _recommendationRepository;
    private readonly IMemoryCache _cache;
    private readonly IMovieMetadataProvider _movieMetadataProvider;
    private readonly ITvShowMetadataProvider _tvShowMetadataProvider;

    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        ILogger<RecommendationService> logger,
        IRecommendationRepository recommendationRepository,
        IDbContextFactory<MediaContext> contextFactory,
        IMemoryCache cache,
        IMovieMetadataProvider movieMetadataProvider,
        ITvShowMetadataProvider tvShowMetadataProvider
    )
    {
        _logger = logger;
        _recommendationRepository = recommendationRepository;
        _contextFactory = contextFactory;
        _cache = cache;
        _movieMetadataProvider = movieMetadataProvider;
        _tvShowMetadataProvider = tvShowMetadataProvider;
    }

    public async Task<List<RecommendationDto>> GetPersonalizedRecommendationsAsync(
        Guid userId,
        string mediaTypeFilter,
        int take = 50,
        CancellationToken ct = default
    )
    {
        bool wantMovie = mediaTypeFilter == MediaTypes.MovieMediaType;
        bool wantTv = mediaTypeFilter == MediaTypes.TvMediaType;
        bool wantAnime = mediaTypeFilter == MediaTypes.AnimeMediaType;

        // Phase 1: Parallel queries — only fetch candidates for the requested type
        Task<List<RecommendationCandidateDto>> movieRecsTask = QueryIf(
            wantMovie,
            () => _recommendationRepository.GetUnownedMovieRecommendationsAsync(userId, ct),
            ct
        );
        Task<List<RecommendationCandidateDto>> tvRecsTask = QueryIf(
            wantTv,
            () => _recommendationRepository.GetUnownedTvRecommendationsAsync(userId, ct),
            ct
        );
        Task<List<RecommendationCandidateDto>> animeRecsTask = QueryIf(
            wantAnime,
            () => _recommendationRepository.GetUnownedAnimeRecommendationsAsync(userId, ct),
            ct
        );
        Task<List<RecommendationCandidateDto>> movieSimTask = QueryIf(
            wantMovie,
            () => _recommendationRepository.GetUnownedMovieSimilarAsync(userId, ct),
            ct
        );
        Task<List<RecommendationCandidateDto>> tvSimTask = QueryIf(
            wantTv,
            () => _recommendationRepository.GetUnownedTvSimilarAsync(userId, ct),
            ct
        );
        Task<List<RecommendationCandidateDto>> animeSimTask = QueryIf(
            wantAnime,
            () => _recommendationRepository.GetUnownedAnimeSimilarAsync(userId, ct),
            ct
        );
        Task<UserAffinityProfile> affinityTask = GetOrBuildAffinityProfileAsync(userId, ct);

        await Task.WhenAll([
            movieRecsTask,
            tvRecsTask,
            animeRecsTask,
            movieSimTask,
            tvSimTask,
            animeSimTask,
            affinityTask,
        ]);

        _logger.LogDebug(
            "Recommendations [{MediaTypeFilter}]: recs={Count}, similar={Count2}, affinity sources={Count3}",
            [
                mediaTypeFilter,
                animeRecsTask.Result.Count + movieRecsTask.Result.Count + tvRecsTask.Result.Count,
                animeSimTask.Result.Count + movieSimTask.Result.Count + tvSimTask.Result.Count,
                affinityTask.Result.SourceItems.Count,
            ]
        );

        UserAffinityProfile profile = affinityTask.Result;

        // Phase 1b: Cross-type keyword candidates from what the user rated, finished or favorited
        (
            Dictionary<int, List<int>> movieKeywordMap,
            Dictionary<int, List<int>> tvKeywordMap,
            Dictionary<int, List<int>> animeKeywordMap
        ) = RecommendationScoring.HighSignalKeywordMaps(profile);

        // Cross-type: use keywords from one type to find candidates in another
        // Anime uses its own keywords to find anime candidates via the TV keyword path (anime is stored as TV)
        Dictionary<int, List<int>> nonMovieKeywordMap = tvKeywordMap
            .Concat(animeKeywordMap)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        Task<List<RecommendationCandidateDto>> crossTypeTvTask = QueryIf(
            wantTv && movieKeywordMap.Count > 0,
            () =>
                _recommendationRepository.GetKeywordCrossTypeTvCandidatesAsync(
                    userId,
                    movieKeywordMap: movieKeywordMap,
                    minSharedKeywords: 3,
                    maxCandidates: 100,
                    ct: ct
                ),
            ct
        );

        Task<List<RecommendationCandidateDto>> crossTypeMovieTask = QueryIf(
            wantMovie && nonMovieKeywordMap.Count > 0,
            () =>
                _recommendationRepository.GetKeywordCrossTypeMovieCandidatesAsync(
                    userId,
                    tvKeywordMap: nonMovieKeywordMap,
                    minSharedKeywords: 3,
                    maxCandidates: 100,
                    ct: ct
                ),
            ct
        );

        Task<List<RecommendationCandidateDto>> crossTypeAnimeTask = QueryIf(
            wantAnime && movieKeywordMap.Count > 0,
            () =>
                _recommendationRepository.GetKeywordCrossTypeAnimeCandidatesAsync(
                    userId,
                    movieKeywordMap: movieKeywordMap,
                    minSharedKeywords: 3,
                    maxCandidates: 100,
                    ct: ct
                ),
            ct
        );

        await Task.WhenAll([crossTypeTvTask, crossTypeMovieTask, crossTypeAnimeTask]);

        // Phase 2: Merge candidates (same MediaId+MediaType from Recommendation + Similar + Keywords = higher frequency)
        List<RecommendationCandidateDto> allCandidates = RecommendationScoring.MergeCandidates([
            movieRecsTask.Result,
            tvRecsTask.Result,
            animeRecsTask.Result,
            movieSimTask.Result,
            tvSimTask.Result,
            animeSimTask.Result,
            crossTypeTvTask.Result,
            crossTypeMovieTask.Result,
            crossTypeAnimeTask.Result,
        ]);

        // Phase 3: Get genre maps for source items — use actual source type from profile, not candidate type
        HashSet<int> allSourceIds = allCandidates.SelectMany(c => c.SourceIds).ToHashSet();
        List<int> allSourceMovieIds = allSourceIds
            .Where(id =>
                profile.SourceItems.TryGetValue(id, out UserAffinitySourceDto? s)
                && s.MediaType == MediaTypes.MovieMediaType
            )
            .ToList();
        List<int> allSourceTvIds = allSourceIds
            .Where(id =>
                profile.SourceItems.TryGetValue(id, out UserAffinitySourceDto? s)
                && s.MediaType != MediaTypes.MovieMediaType
            )
            .ToList();

        Task<Dictionary<int, List<int>>> movieGenreMapTask = Task.Run(
            () => _recommendationRepository.GetGenresForMovieIdsAsync(allSourceMovieIds, ct),
            ct
        );
        Task<Dictionary<int, List<int>>> tvGenreMapTask = Task.Run(
            () => _recommendationRepository.GetGenresForTvIdsAsync(allSourceTvIds, ct),
            ct
        );

        await Task.WhenAll([movieGenreMapTask, tvGenreMapTask]);

        Dictionary<int, List<int>> combinedGenreMap = new(movieGenreMapTask.Result);
        foreach (KeyValuePair<int, List<int>> kv in tvGenreMapTask.Result)
            combinedGenreMap[kv.Key] = kv.Value;

        // Phase 4: Score all candidates
        List<RecommendationDto> scored = allCandidates
            .Select(c => new RecommendationDto
            {
                Id = c.MediaId,
                Title = c.Title,
                TitleSort = c.TitleSort,
                Overview = c.Overview,
                Poster = c.Poster,
                Backdrop = c.Backdrop,
                ColorPalette = ColorPalette.FromJsonOrNull(c.ColorPalette),
                Type = c.MediaType,
                Score = RecommendationScoring.ScoreCandidate(c, profile, combinedGenreMap),
                SourceCount = c.SourceCount,
                SourceIds = c.SourceIds,
            })
            .Where(s => s.Poster != null)
            .ToList();

        // Deduplicate by Id — same TMDB ID may appear as both tv and anime; keep highest-scored
        List<RecommendationDto> deduped = scored
            .GroupBy(s => s.Id)
            .Select(g => g.OrderByDescending(s => s.Score).First())
            .ToList();

        _logger.LogDebug(
            "Recommendations [{MediaTypeFilter}]: merged={Count}, scored={Count2}, deduped={Count3}",
            [mediaTypeFilter, allCandidates.Count, scored.Count, deduped.Count]
        );

        // Phase 5: Diversity selection — guarantee floor representation per media type
        return RecommendationScoring.SelectWithDiversity(
            deduped,
            take,
            item => item.Type,
            item => item.Score
        );
    }

    public async Task<List<RecommendationDto>> GetHomeRecommendationCarouselAsync(
        Guid userId,
        string mediaTypeFilter,
        int take = 36,
        CancellationToken ct = default
    )
    {
        return await GetPersonalizedRecommendationsAsync(userId, mediaTypeFilter, take, ct);
    }

    public async Task<RecommendationDetailDto?> GetRecommendationDetailAsync(
        Guid userId,
        int mediaId,
        string mediaType,
        string country,
        string language,
        CancellationToken ct = default
    )
    {
        bool isMovie = mediaType == "movie";
        string tmdbLanguage = $"{language}-{country}";

        // Fetch TMDB data and local source items in parallel
        Task<TmdbMovieAppends?> movieAppendsTask = isMovie
            ? _movieMetadataProvider.GetMovieAsync(mediaId, tmdbLanguage, ct)
            : Task.FromResult<TmdbMovieAppends?>(null);
        Task<TmdbTvShowAppends?> tvAppendsTask = !isMovie
            ? _tvShowMetadataProvider.GetTvShowAsync(mediaId, tmdbLanguage, ct)
            : Task.FromResult<TmdbTvShowAppends?>(null);

        Task<(List<Movie> Movies, string? ColorPalette)> sourceMoviesTask = isMovie
            ? _recommendationRepository.GetSourceMoviesForMediaAsync(userId, mediaId, ct)
            : Task.FromResult<(List<Movie>, string?)>(([], null));
        Task<(List<Tv> TvShows, string? ColorPalette)> sourceTvsTask = !isMovie
            ? _recommendationRepository.GetSourceTvShowsForMediaAsync(userId, mediaId, ct)
            : Task.FromResult<(List<Tv>, string?)>(([], null));

        await Task.WhenAll([movieAppendsTask, tvAppendsTask, sourceMoviesTask, sourceTvsTask]);

        // Keyword-based source enrichment: same-type (exclude already-found Rec/Similar sources) + cross-type
        HashSet<int> existingMovieSourceIds = sourceMoviesTask
            .Result.Movies.Select(m => m.Id)
            .ToHashSet();
        HashSet<int> existingTvSourceIds = sourceTvsTask
            .Result.TvShows.Select(t => t.Id)
            .ToHashSet();

        List<Movie> keywordMovieSources = isMovie
            ? await _recommendationRepository.GetKeywordMovieSourcesForMovieAsync(
                userId,
                mediaId,
                existingMovieSourceIds,
                ct
            )
            : await _recommendationRepository.GetCrossTypeMovieSourcesForTvAsync(
                userId,
                mediaId,
                ct
            );
        List<Tv> keywordTvSources = !isMovie
            ? await _recommendationRepository.GetKeywordTvSourcesForTvAsync(
                userId,
                mediaId,
                existingTvSourceIds,
                ct
            )
            : await _recommendationRepository.GetCrossTypeTvSourcesForMovieAsync(
                userId,
                mediaId,
                ct
            );

        string? rawPalette = isMovie
            ? sourceMoviesTask.Result.ColorPalette
            : sourceTvsTask.Result.ColorPalette;
        ColorPalette? colorPalette = ColorPalette.FromJsonOrNull(rawPalette);

        if (isMovie)
        {
            TmdbMovieAppends? appends = movieAppendsTask.Result;
            if (appends is null)
                return null;

            // Same-type keyword sources (e.g., Ice Age movies for an Ice Age spinoff), then
            // cross-type TV sources found via keyword overlap, capped per title family.
            List<RecommendationDetailSourceDto> becauseYouHave = DeduplicateSourcesByFamily([
                .. sourceMoviesTask.Result.Movies.Select(SourceFrom),
                .. keywordMovieSources.Select(SourceFrom),
                .. keywordTvSources.Select(SourceFrom),
            ]);

            return new()
            {
                Id = appends.Id,
                Title = appends.Title,
                Overview = appends.Overview,
                Poster = appends.PosterPath,
                Backdrop = appends.BackdropPath,
                Logo = appends
                    .Images.Logos.Where(l => l.Iso6391 == "en")
                    .OrderByDescending(l => l.VoteAverage)
                    .FirstOrDefault()
                    ?.FilePath,
                ColorPalette = colorPalette,
                MediaType = "movie",
                Year = appends.ReleaseDate?.Year,
                VoteAverage = appends.VoteAverage,
                Genres = appends.Genres.Select(g => new GenreDto(g)),
                ContentRatings = appends
                    .ReleaseDates.Results.Where(r => r.Iso31661 == country)
                    .SelectMany(r => r.ReleaseDates)
                    .Where(rd => !string.IsNullOrEmpty(rd.Certification))
                    .Select(rd => new ContentRating
                    {
                        Rating = rd.Certification,
                        Iso31661 = country,
                    })
                    .DistinctBy(cr => cr.Rating),
                ExternalIds = new() { ImdbId = appends.ExternalIds.ImdbId },
                BecauseYouHave = becauseYouHave,
            };
        }
        else
        {
            TmdbTvShowAppends? appends = tvAppendsTask.Result;
            if (appends is null)
                return null;

            // Same-type keyword sources, then cross-type movie sources found via keyword
            // overlap, capped per title family.
            List<RecommendationDetailSourceDto> becauseYouHave = DeduplicateSourcesByFamily([
                .. sourceTvsTask.Result.TvShows.Select(SourceFrom),
                .. keywordTvSources.Select(SourceFrom),
                .. keywordMovieSources.Select(SourceFrom),
            ]);

            return new()
            {
                Id = appends.Id,
                Title = appends.Name,
                Overview = appends.Overview,
                Poster = appends.PosterPath,
                Backdrop = appends.BackdropPath,
                Logo = appends
                    .Images.Logos.Where(l => l.Iso6391 == "en")
                    .OrderByDescending(l => l.VoteAverage)
                    .FirstOrDefault()
                    ?.FilePath,
                ColorPalette = colorPalette,
                MediaType = "tv",
                Year = appends.FirstAirDate?.Year,
                VoteAverage = appends.VoteAverage,
                Genres = appends.Genres.Select(g => new GenreDto(g)),
                ContentRatings = appends
                    .ContentRatings.Results.Where(cr => cr.Iso31661 == country)
                    .Select(cr => new ContentRating { Rating = cr.Rating, Iso31661 = cr.Iso31661 }),
                ExternalIds = new()
                {
                    ImdbId = appends.ExternalIds.ImdbId,
                    TvdbId = appends.ExternalIds.TvdbId,
                },
                BecauseYouHave = becauseYouHave,
            };
        }
    }

    private static Task<List<RecommendationCandidateDto>> QueryIf(
        bool wanted,
        Func<Task<List<RecommendationCandidateDto>>> query,
        CancellationToken ct
    ) => wanted ? Task.Run(query, ct) : Task.FromResult(new List<RecommendationCandidateDto>());

    private static RecommendationDetailSourceDto SourceFrom(Movie m) =>
        new RecommendationDetailSourceDto
        {
            Id = m.Id,
            Title = m.Title,
            TitleSort = m.TitleSort,
            Poster = m.Poster,
            Backdrop = m.Backdrop,
            Logo = m.Images.FirstOrDefault()?.FilePath,
            Overview = m.Overview,
            Year = m.ReleaseDate?.Year,
            ColorPalette = m.ColorPalette,
            MediaType = "movie",
            HaveItems = m.VideoFiles.Count(vf => vf.Folder != null),
            NumberOfItems = 1,
            Duration = m.Runtime ?? 0,
            Tags = m.KeywordMovies.Select(km => km.Keyword.Name),
        };

    private static RecommendationDetailSourceDto SourceFrom(Tv t) =>
        new RecommendationDetailSourceDto
        {
            Id = t.Id,
            Title = t.Title,
            TitleSort = t.TitleSort,
            Poster = t.Poster,
            Backdrop = t.Backdrop,
            Logo = t.Images.FirstOrDefault()?.FilePath,
            Overview = t.Overview,
            Year = t.FirstAirDate?.Year,
            ColorPalette = t.ColorPalette,
            MediaType = "tv",
            HaveItems = t.Episodes.Count(e =>
                e.SeasonNumber > 0 && e.VideoFiles.Any(vf => vf.Folder != null)
            ),
            NumberOfItems = t.Episodes.Count(e => e.SeasonNumber > 0),
            Duration = t.Duration ?? 0,
            Tags = t.KeywordTvs.Select(kt => kt.Keyword.Name),
        };

    /// <summary>
    /// Limits because_you_have items to max 3 per title family.
    /// Prevents 18 Tom and Jerry items from drowning out more relevant sources like Ice Age movies.
    /// </summary>
    private static List<RecommendationDetailSourceDto> DeduplicateSourcesByFamily(
        List<RecommendationDetailSourceDto> sources,
        int maxPerFamily = 3
    )
    {
        if (sources.Count <= maxPerFamily)
            return sources;

        List<string> families = [];
        List<(string Family, RecommendationDetailSourceDto Source)> tagged =
        [
            .. sources.Select(source =>
                (TitleFamily.Assign(source.Title.OrEmpty(), families), source)
            ),
        ];

        // Take up to maxPerFamily items from each family, then flatten
        return tagged
            .GroupBy(t => t.Family)
            .SelectMany(g => g.Take(maxPerFamily).Select(t => t.Source))
            .ToList();
    }

    private async Task<UserAffinityProfile> GetOrBuildAffinityProfileAsync(
        Guid userId,
        CancellationToken ct
    )
    {
        string cacheKey = $"reco:affinity:{userId}";

        if (_cache.TryGetValue(cacheKey, out UserAffinityProfile? cached) && cached is not null)
            return cached;

        Task<List<UserAffinitySourceDto>> movieAffinityTask = Task.Run(
            async () =>
            {
                return await _recommendationRepository.GetUserMovieAffinityDataAsync(userId, ct);
            },
            ct
        );
        Task<List<UserAffinitySourceDto>> tvAffinityTask = Task.Run(
            async () =>
            {
                return await _recommendationRepository.GetUserTvAffinityDataAsync(userId, ct);
            },
            ct
        );
        Task<List<UserAffinitySourceDto>> animeAffinityTask = Task.Run(
            async () =>
            {
                return await _recommendationRepository.GetUserAnimeAffinityDataAsync(userId, ct);
            },
            ct
        );

        await Task.WhenAll([movieAffinityTask, tvAffinityTask, animeAffinityTask]);

        List<UserAffinitySourceDto> allSources = movieAffinityTask
            .Result.Concat(tvAffinityTask.Result)
            .Concat(animeAffinityTask.Result)
            .ToList();

        UserAffinityProfile profile = RecommendationScoring.BuildProfile(allSources);

        MemoryCacheEntryOptions cacheOptions = new()
        {
            SlidingExpiration = TimeSpan.FromMinutes(10),
            Size = 1,
        };
        _cache.Set(cacheKey, profile, cacheOptions);

        return profile;
    }
}
