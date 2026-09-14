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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.Services;

public class HomeService(
    IHomeRepository homeRepository,
    ILibraryRepository libraryRepository,
    IDbContextFactory<MediaContext> contextFactory
)
{
    /// <summary>
    /// Render variant used by the mobile and TV clients, which build their own library rows.
    /// </summary>
    private const string LolomoVersion = "lolomo";

    /// <summary>
    /// A "Latest in {library}" row per library, newest first, highest library order first.
    /// </summary>
    public async Task<List<GenreRowDto<GenreRowItemDto>>> GetLatestInLibraryRowsAsync(
        Guid userId,
        string language,
        string country,
        CancellationToken ct
    )
    {
        List<Library> libraries = await libraryRepository.GetLibrariesLite(userId, ct);

        // Fetch all library data in parallel - each task needs its own MediaContext for thread safety
        Task<(Library library, List<Movie> movies, List<Tv> shows)>[] libraryDataTasks =
        [
            .. libraries.Select(async library =>
            {
                await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);
                List<Movie> libraryMovies = [];
                await foreach (
                    Movie movie in libraryRepository
                        .GetLibraryMovies(
                            context,
                            userId,
                            library.Id,
                            language,
                            UiLimits.MaximumCardsInCarousel,
                            0,
                            m => m.CreatedAt,
                            "desc"
                        )
                        .WithCancellation(ct)
                )
                {
                    libraryMovies.Add(movie);
                }

                List<Tv> libraryShows = [];
                await foreach (
                    Tv tv in libraryRepository
                        .GetLibraryShows(
                            context,
                            userId,
                            library.Id,
                            language,
                            UiLimits.MaximumCardsInCarousel,
                            0,
                            m => m.CreatedAt,
                            "desc"
                        )
                        .WithCancellation(ct)
                )
                {
                    libraryShows.Add(tv);
                }

                return (library, libraryMovies, libraryShows);
            }),
        ];

        (Library library, List<Movie> movies, List<Tv> shows)[] libraryDataResults =
            await Task.WhenAll(libraryDataTasks);

        return
        [
            .. libraryDataResults
                .OrderByDescending(r => r.library.Order)
                .Select(r => new GenreRowDto<GenreRowItemDto>
                {
                    Title = "Latest in " + r.library.Title,
                    MoreLink = new($"/libraries/{r.library.Id}", UriKind.Relative),
                    Items = r
                        .movies.Select(movie => new GenreRowItemDto(movie, country))
                        .Concat(r.shows.Select(tv => new GenreRowItemDto(tv, country))),
                }),
        ];
    }

    public async Task<List<GenreRowDto<GenreRowItemDto>>> GetHomePageContent(
        Guid userId,
        string language,
        string country,
        PageRequestDto request
    )
    {
        List<Genre> genreItems = await homeRepository.GetHome(
            userId,
            language,
            request.Take,
            request.Page
        );

        List<GenreRowDto<GenreRowItemDto>> genres = FetchGenres(genreItems).ToList();

        List<int> movieIds = genres
            .SelectMany(genreRow =>
                genreRow
                    .Source.Where(homeSource => homeSource.MediaType == MediaTypes.MovieMediaType)
                    .Select(h => h.Id)
            )
            .ToList();

        List<int> tvIds = genres
            .SelectMany(genre =>
                genre
                    .Source.Where(source => source.MediaType == MediaTypes.TvMediaType)
                    .Select(source => source.Id)
            )
            .ToList();

        HomeTvsAndMoviesData tvsAndMovies = await homeRepository.GetHomeTvsAndMoviesAsync(
            tvIds,
            movieIds,
            language,
            country
        );

        foreach (GenreRowDto<GenreRowItemDto> genre in genres)
        {
            genre.Items = genre
                .Source.Select(source =>
                    TransformToRowItemDto(source, tvsAndMovies.TvData, tvsAndMovies.MovieData)
                )
                .Where(genreRow => genreRow != null);
        }

        return genres.Where(genre => genre.Items.Any()).ToList();
    }

    private static GenreRowItemDto? TransformToRowItemDto(
        HomeSourceDto source,
        List<HomeTvCardDto> tvData,
        List<HomeMovieCardDto> movieData
    )
    {
        return source.MediaType switch
        {
            MediaTypes.TvMediaType => tvData.FirstOrDefault(t => t.Id == source.Id) is { } tv
                ? new GenreRowItemDto(tv)
                : null,
            MediaTypes.MovieMediaType => movieData.FirstOrDefault(m => m.Id == source.Id)
                is { } movie
                ? new GenreRowItemDto(movie)
                : null,
            _ => null,
        };
    }

    private IEnumerable<GenreRowDto<GenreRowItemDto>> FetchGenres(List<Genre> genreItems)
    {
        return from genre in genreItems
            let name = genre.Translations.FirstOrDefault()?.Name ?? genre.Name
            select new GenreRowDto<GenreRowItemDto>
            {
                Title = name,
                MoreLink = new($"/genres/{genre.Id}", UriKind.Relative),
                Id = genre.Id.ToString(),
                Source = genre
                    .GenreMovies.Select(movie => new HomeSourceDto(
                        movie.MovieId,
                        MediaTypes.MovieMediaType
                    ))
                    .Concat(
                        genre.GenreTvShows.Select(tv => new HomeSourceDto(
                            tv.TvId,
                            MediaTypes.TvMediaType
                        ))
                    )
                    .Randomize()
                    .Take(UiLimits.MaximumCardsInCarousel),
            };
    }

    public async Task<ComponentResponse> GetHomeData(
        Guid userId,
        string language,
        string country,
        string? version = null
    )
    {
        HomeParallelData parallelData = await homeRepository.GetHomeParallelDataAsync(
            userId,
            language,
            country
        );

        if (parallelData is { MovieCount: 0, TvCount: 0, AnimeCount: 0 })
            return new()
            {
                Data = [EmptyHomeState(hasLibraries: parallelData.Libraries.Count > 0)],
            };

        (List<GenreSourceData> genreSources, List<int> movieIds, List<int> tvIds) =
            PickGenreSources(parallelData.GenreItems);

        // "Latest in {library}" belongs to the desktop home only; the lolomo clients lay
        // their library rows out themselves, so these would be duplicate content there.
        // An empty list also keeps the prev/next chain below free of rows never emitted.
        Task<List<GenreCarouselData>> libraryCarouselsTask =
            version == LolomoVersion
                ? Task.FromResult<List<GenreCarouselData>>([])
                : LoadLibraryCarouselsAsync(userId, country, parallelData);

        Task<HomeTvsAndMoviesData> tvsAndMoviesTask = homeRepository.GetHomeTvsAndMoviesAsync(
            tvIds,
            movieIds,
            language,
            country
        );

        await Task.WhenAll(tvsAndMoviesTask, libraryCarouselsTask);

        HomeTvsAndMoviesData tvsAndMovies = tvsAndMoviesTask.Result;
        List<GenreCarouselData> libraryCarousels = libraryCarouselsTask.Result;

        List<GenreCarouselData> genreCarousels = genreSources
            .Select(g => new GenreCarouselData(
                g.Id,
                g.Title,
                g.MoreLink,
                g.Source.Select(source =>
                        ResolveCardData(source, tvsAndMovies.TvData, tvsAndMovies.MovieData)
                    )
                    .Where(c => c != null)
                    .Cast<CardData>()
                    .ToList()
            ))
            .Where(g => g.Items.Count > 0)
            .ToList();

        CardData? homeCardItem = genreCarousels
            .Where(g => !string.IsNullOrEmpty(g.Title))
            .SelectMany(g => g.Items)
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .Randomize()
            .FirstOrDefault();

        List<ComponentEnvelope> components = [];

        if (homeCardItem != null)
        {
            components.Add(
                Component
                    .HomeCard(await BuildHeroAsync(homeCardItem, language))
                    .WithUpdate("pageLoad", "/home/card")
                    .Build()
            );
        }

        // Navigation chain: continue → library_* → genre_* → continue (circular)
        HashSet<UserData> continueWatching = parallelData.ContinueWatching;
        string? continueId = continueWatching.Count > 0 ? "continue" : null;

        List<(string Id, string Title, GenreCarouselData Data)> carousels =
        [
            .. libraryCarousels.Select(library =>
                ($"library_{library.Id}", $"Latest in {library.Title}", library)
            ),
            .. genreCarousels.Select(genre => ($"genre_{genre.Id}", genre.Title, genre)),
        ];
        List<string> carouselIds = [.. carousels.Select(carousel => carousel.Id)];

        if (continueId is not null)
        {
            (string? lastCarouselId, string? afterContinueId) = HomeCarouselNavigation.ForContinue(
                carouselIds
            );
            components.Add(
                Component
                    .Carousel()
                    .WithId(continueId)
                    .WithNavigation(lastCarouselId, afterContinueId)
                    .WithTitle("Continue watching".Localize())
                    .WithUpdate("pageLoad", "/home/continue")
                    .WithItems(BuildContinueWatchingCards(continueWatching, country))
                    .Build()
            );
        }

        for (int i = 0; i < carousels.Count; i++)
        {
            (string id, string title, GenreCarouselData data) = carousels[i];
            (string? prevId, string? nextId) = HomeCarouselNavigation.ForCarousel(
                carouselIds,
                i,
                continueId
            );

            components.Add(
                Component
                    .Carousel()
                    .WithId(id)
                    .WithNavigation(prevId, nextId)
                    .WithTitle(title)
                    .WithMoreLink(data.MoreLink)
                    .WithItems(data.Items.Select(item => Component.Card(item).Build()))
                    .Build()
            );
        }

        return new() { Data = components };
    }

    private static ComponentEnvelope EmptyHomeState(bool hasLibraries) =>
        hasLibraries
            ? Component
                .EmptyState(
                    new()
                    {
                        Title = "Scanning your libraries",
                        Message =
                            "Content will appear as it's found. This usually takes a few minutes.",
                        Icon = "scanning",
                        AutoRefresh = true,
                    }
                )
                .Build()
            : Component
                .EmptyState(
                    new()
                    {
                        Title = "No libraries yet",
                        Message = "Create your first library to get started.",
                        Icon = "library",
                        Action = new() { Label = "Add library", Route = "/dashboard/libraries" },
                    }
                )
                .Build();

    /// <summary>
    /// A random carousel's worth of titles per genre, and every movie and show id
    /// those picks need loaded.
    /// </summary>
    private static (
        List<GenreSourceData> Sources,
        List<int> MovieIds,
        List<int> TvIds
    ) PickGenreSources(List<GenreHomeDto> genreItems)
    {
        List<GenreSourceData> sources = [];
        List<int> movieIds = [];
        List<int> tvIds = [];

        foreach (GenreHomeDto genre in genreItems)
        {
            List<HomeSourceDto> source =
            [
                .. genre
                    .MovieIds.Select(id => new HomeSourceDto(id, MediaTypes.MovieMediaType))
                    .Concat(genre.TvIds.Select(id => new HomeSourceDto(id, MediaTypes.TvMediaType)))
                    .Randomize()
                    .Take(UiLimits.MaximumCardsInCarousel),
            ];

            tvIds.AddRange(
                source.Where(s => s.MediaType == MediaTypes.TvMediaType).Select(s => s.Id)
            );
            movieIds.AddRange(
                source.Where(s => s.MediaType == MediaTypes.MovieMediaType).Select(s => s.Id)
            );

            sources.Add(
                new(
                    genre.Id.ToString(),
                    genre.TranslatedName ?? genre.Name,
                    new($"/genres/{genre.Id}", UriKind.Relative),
                    source
                )
            );
        }

        return (sources, movieIds, tvIds);
    }

    /// <summary>
    /// The newest titles of each library. Each repository call owns its own context,
    /// so the per-library fan-out runs in parallel.
    /// </summary>
    private async Task<List<GenreCarouselData>> LoadLibraryCarouselsAsync(
        Guid userId,
        string country,
        HomeParallelData parallelData
    )
    {
        (Library Library, List<MovieCardDto> Movies, List<TvCardDto> Shows)[] results =
            await Task.WhenAll(
                parallelData.Libraries.Select(async library =>
                    (
                        library,
                        await libraryRepository.GetLibraryMovieCardsAsync(
                            userId,
                            library.Id,
                            country,
                            UiLimits.MaximumCardsInCarousel,
                            0
                        ),
                        await libraryRepository.GetLibraryTvCardsAsync(
                            userId,
                            library.Id,
                            country,
                            UiLimits.MaximumCardsInCarousel,
                            0
                        )
                    )
                )
            );

        List<GenreCarouselData> carousels = [];
        foreach ((Library library, List<MovieCardDto> movies, List<TvCardDto> shows) in results)
        {
            List<CardData> items =
            [
                .. movies
                    .Select(m => new CardData(m))
                    .Concat(shows.Select(t => new CardData(t)))
                    .OrderByDescending(c => c.CreatedAt),
            ];
            if (items.Count == 0)
                continue;

            int itemCount = library.Type switch
            {
                MediaTypes.MovieMediaType => parallelData.MovieCount,
                MediaTypes.TvMediaType => parallelData.TvCount,
                MediaTypes.AnimeMediaType => parallelData.AnimeCount,
                _ => 0,
            };
            Uri moreLink =
                itemCount > UiLimits.MaximumItemsPerPage
                    ? new($"/libraries/{library.Id}/letter/A", UriKind.Relative)
                    : new($"/libraries/{library.Id}", UriKind.Relative);

            carousels.Add(new(library.Id.ToString(), library.Title, moreLink, items));
        }

        return carousels;
    }

    private static CardData? ResolveCardData(
        HomeSourceDto source,
        List<HomeTvCardDto> tvData,
        List<HomeMovieCardDto> movieData,
        bool watch = false
    )
    {
        return source.MediaType switch
        {
            MediaTypes.TvMediaType => tvData.FirstOrDefault(t => t.Id == source.Id) is { } tv
                ? new CardData(tv, watch)
                : null,
            MediaTypes.MovieMediaType => movieData.FirstOrDefault(m => m.Id == source.Id)
                is { } movie
                ? new CardData(movie, watch)
                : null,
            _ => null,
        };
    }

    private static IEnumerable<ComponentEnvelope> BuildContinueWatchingCards(
        IEnumerable<UserData> continueWatching,
        string country
    )
    {
        return continueWatching
            .Select(item =>
                Component
                    .Card(new(item, country))
                    .WithWatch()
                    .WithContextMenu([
                        new()
                        {
                            Id = "remove_continue_watching",
                            Title = "Remove from continue watching".Localize(),
                            Icon = "mooooom-trash",
                            Method = "DELETE",
                            Destructive = true,
                            Confirm =
                                "Are you sure you want to remove this from continue watching?".Localize(),
                            Args = new()
                            {
                                { "url", "/userData/continue" },
                                { "replaceKey", "home" },
                            },
                        },
                    ])
                    .Build()
            )
            .DistinctBy(c => ((LeafProps<CardData>)c.Props).Data?.Link);
    }

    public async Task<ComponentResponse> GetHomeCard(
        Guid userId,
        string language,
        string country,
        Ulid replaceId
    )
    {
        HomeTvCardDto? tv = await libraryRepository.GetRandomTvCardAsync(userId, language, country);
        HomeMovieCardDto? movie = await libraryRepository.GetRandomMovieCardAsync(
            userId,
            language,
            country
        );

        List<CardData> candidates = [];
        if (tv != null)
            candidates.Add(new(tv));
        if (movie != null)
            candidates.Add(new(movie));

        CardData? homeCardItem = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .Randomize()
            .FirstOrDefault();

        HomeCardData hero =
            homeCardItem != null ? await BuildHeroAsync(homeCardItem, language) : new();

        return new()
        {
            Data =
            [
                Component
                    .HomeCard(hero)
                    .WithUpdate("pageLoad", "/home/card")
                    .WithReplacing(replaceId)
                    .Build(),
            ],
        };
    }

    /// <summary>
    /// Turns the picked title into the home hero, with the artwork a hero wants rather than the
    /// artwork a grid card wants.
    /// </summary>
    /// <remarks>
    /// A carousel card carries the poster TMDB nominates for the title, which normally has the
    /// name printed on it — right for a card an inch wide with its title underneath, wrong for
    /// a hero that writes the name itself. Both hero endpoints go through here so the two
    /// cannot answer the same question differently; the card's own artwork stays as the floor
    /// for a title that has no image rows at all.
    /// </remarks>
    private async Task<HomeCardData> BuildHeroAsync(CardData item, string language)
    {
        object? id = item.Id;

        HeroArtwork? artwork = id is int mediaId
            ? await homeRepository.GetHeroArtworkAsync(mediaId, item.Type, language)
            : null;

        return new()
        {
            Id = item.Id,
            Title = item.Title,
            Overview = item.Overview,
            Backdrop = item.Backdrop,
            Poster = artwork?.Poster ?? item.Poster,
            PosterIsTextless = artwork?.PosterIsTextless ?? false,
            Logo = artwork?.Logo ?? item.Logo,
            Year = item.Year,
            ColorPalette = item.ColorPalette,
            Link = item.Link,
            MediaType = item.Type,
        };
    }

    public async Task<ScreensaverDto> GetSetupScreensaverContent(Guid userId)
    {
        HashSet<Image> data = await homeRepository.GetScreensaverImagesAsync(userId);

        // Logo lookups built once. The old per-backdrop FirstOrDefault over a lazy
        // logo filter re-scanned every image for each backdrop (O(backdrops x images)),
        // seconds of CPU on a large library. Index the logos by title id instead.
        Dictionary<int, Image> logoByTv = data.Where(image =>
                image is { Type: "logo", TvId: not null }
            )
            .GroupBy(image => image.TvId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<int, Image> logoByMovie = data.Where(image =>
                image is { Type: "logo", MovieId: not null }
            )
            .GroupBy(image => image.MovieId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        IEnumerable<ScreensaverDataDto> tvCollection = data.Where(image =>
                image is { TvId: not null, Type: "backdrop" }
            )
            .DistinctBy(image => image.TvId)
            .Select(image => new ScreensaverDataDto(
                image,
                logoByTv.GetValueOrDefault(image.TvId!.Value)
            ));

        IEnumerable<ScreensaverDataDto> movieCollection = data.Where(image =>
                image is { MovieId: not null, Type: "backdrop" }
            )
            .DistinctBy(image => image.MovieId)
            .Select(image => new ScreensaverDataDto(
                image,
                logoByMovie.GetValueOrDefault(image.MovieId!.Value)
            ));

        return new()
        {
            Data = tvCollection
                .Concat(movieCollection)
                .Where(image => image.Meta?.Logo != null)
                .Randomize(),
        };
    }

    public async Task<ComponentResponse> GetHomeTvContent(
        Guid userId,
        string language,
        string country
    )
    {
        HashSet<UserData> continueWatching = await homeRepository.GetContinueWatchingAsync(
            userId,
            language,
            country
        );

        // Collect genre source data
        List<GenreSourceData> genreSourceList = [];
        List<int> movieIds = [];
        List<int> tvIds = [];

        List<GenreHomeDto> genreItems = await homeRepository.GetHomeGenresAsync(
            userId,
            language,
            UiLimits.MaximumItemsPerPage
        );

        foreach (GenreHomeDto genre in genreItems)
        {
            IEnumerable<HomeSourceDto> movies = genre.MovieIds.Select(id => new HomeSourceDto(
                id,
                MediaTypes.MovieMediaType
            ));
            IEnumerable<HomeSourceDto> tvs = genre.TvIds.Select(id => new HomeSourceDto(
                id,
                MediaTypes.TvMediaType
            ));

            string name = genre.TranslatedName ?? genre.Name;
            List<HomeSourceDto> source = movies
                .Concat(tvs)
                .Randomize()
                .Take(UiLimits.MaximumCardsInCarousel)
                .ToList();

            tvIds.AddRange(
                source.Where(s => s.MediaType == MediaTypes.TvMediaType).Select(s => s.Id)
            );
            movieIds.AddRange(
                source.Where(s => s.MediaType == MediaTypes.MovieMediaType).Select(s => s.Id)
            );

            genreSourceList.Add(
                new(genre.Id.ToString(), name, new($"/genres/{genre.Id}", UriKind.Relative), source)
            );
        }

        // Fetch data
        HomeTvsAndMoviesData tvsAndMovies = await homeRepository.GetHomeTvsAndMoviesAsync(
            tvIds,
            movieIds,
            language,
            country
        );

        // Build genre carousels
        List<GenreCarouselData> genreCarousels = genreSourceList
            .Select(g => new GenreCarouselData(
                g.Id,
                g.Title,
                g.MoreLink,
                g.Source.Select(source =>
                        ResolveCardData(
                            source,
                            tvsAndMovies.TvData,
                            tvsAndMovies.MovieData,
                            watch: false
                        )
                    )
                    .Where(c => c != null)
                    .Cast<CardData>()
                    .ToList()
            ))
            .Where(g => g.Items.Count > 0)
            .ToList();

        // Build components
        List<ComponentEnvelope> components = [];

        // Continue watching
        components.Add(
            Component
                .Carousel()
                .WithId("continue")
                .WithTitle("Continue watching".Localize())
                .WithUpdate("pageLoad", "/home/continue")
                .WithItems(BuildContinueWatchingCards(continueWatching, country))
                .Build()
        );

        // Genre carousels (limited to 6 items for TV)
        foreach (GenreCarouselData genre in genreCarousels)
        {
            components.Add(
                Component
                    .Carousel()
                    .WithId($"genre_{genre.Id}")
                    .WithTitle(genre.Title)
                    .WithMoreLink(genre.MoreLink)
                    .WithItems(genre.Items.Take(6).Select(item => Component.Card(item).Build()))
                    .Build()
            );
        }

        return new() { Data = components };
    }

    public async Task<ComponentResponse> GetHomeContinueContent(
        Guid userId,
        string language,
        string country,
        Ulid replaceId
    )
    {
        HashSet<UserData> continueWatching = await homeRepository.GetContinueWatchingAsync(
            userId,
            language,
            country
        );

        IEnumerable<UserData> filtered = continueWatching.Where(item =>
            item.Tv?.Episodes.LastOrDefault()?.VideoFiles.FirstOrDefault()?.Id != item.VideoFileId
            || item.Time < (item.VideoFile.Duration?.ToSeconds() ?? 0) * 0.8
        );

        return new()
        {
            Data =
            [
                Component
                    .Carousel()
                    .WithId("continue")
                    .WithNavigation("continue", "28")
                    .WithTitle("Continue watching".Localize())
                    .WithUpdate("pageLoad", "/home/continue")
                    .WithItems(BuildContinueWatchingCards(filtered, country))
                    .WithReplacing(replaceId)
                    .Build(),
            ],
        };
    }

    // Helper records for intermediate data
    private record GenreSourceData(
        string Id,
        string Title,
        Uri MoreLink,
        List<HomeSourceDto> Source
    );

    private record GenreCarouselData(string Id, string Title, Uri MoreLink, List<CardData> Items);
}
