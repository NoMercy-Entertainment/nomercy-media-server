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

using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Services;

public class HomeService(IHomeRepository homeRepository, ILibraryRepository libraryRepository)
{
    /// <summary>
    /// Render variant used by the mobile and TV clients, which build their own library rows.
    /// </summary>
    private const string LolomoVersion = "lolomo";

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
        // Phase 1: Run initial independent queries in parallel — repository owns each DbContext
        HomeParallelData parallelData = await homeRepository.GetHomeParallelDataAsync(
            userId,
            language,
            country
        );

        HashSet<UserData> continueWatching = parallelData.ContinueWatching;
        List<GenreHomeDto> genreItems = parallelData.GenreItems;
        List<Library> libraries = parallelData.Libraries;
        int animeCount = parallelData.AnimeCount;
        int movieCount = parallelData.MovieCount;
        int tvCount = parallelData.TvCount;

        // Early-exit: return an NMEmptyState component when there is nothing to show
        bool hasNoContent = movieCount == 0 && tvCount == 0 && animeCount == 0;

        if (hasNoContent)
        {
            ComponentEnvelope emptyState =
                libraries.Count == 0
                    ? Component
                        .EmptyState(
                            new()
                            {
                                Title = "No libraries yet",
                                Message = "Create your first library to get started.",
                                Icon = "library",
                                Action = new()
                                {
                                    Label = "Add library",
                                    Route = "/dashboard/libraries",
                                },
                            }
                        )
                        .Build()
                    : Component
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
                        .Build();

            return new() { Data = [emptyState] };
        }

        // Phase 2: Collect genre source data (sync, fast - just shuffling IDs)
        List<GenreSourceData> genreSourceList = [];
        List<int> movieIds = [];
        List<int> tvIds = [];

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

        // Phase 3: Fetch genre media data and, in parallel, the per-library
        // "Latest in {library}" cards. Each repository call owns its own context,
        // so the fan-out stays concurrency-safe without the service touching a
        // DbContext.
        Task<HomeTvsAndMoviesData> tvsAndMoviesTask = homeRepository.GetHomeTvsAndMoviesAsync(
            tvIds,
            movieIds,
            language,
            country
        );

        List<
            Task<(Library Library, List<MovieCardDto> Movies, List<TvCardDto> Shows)>
        > libraryTasks = libraries
            .Select(async library =>
            {
                List<MovieCardDto> libraryMovies =
                    await libraryRepository.GetLibraryMovieCardsAsync(
                        userId,
                        library.Id,
                        country,
                        UiLimits.MaximumCardsInCarousel,
                        0
                    );
                List<TvCardDto> libraryShows = await libraryRepository.GetLibraryTvCardsAsync(
                    userId,
                    library.Id,
                    country,
                    UiLimits.MaximumCardsInCarousel,
                    0
                );
                return (library, libraryMovies, libraryShows);
            })
            .ToList();

        await Task.WhenAll(tvsAndMoviesTask, Task.WhenAll(libraryTasks));

        HomeTvsAndMoviesData tvsAndMovies = tvsAndMoviesTask.Result;
        List<HomeTvCardDto> tvData = tvsAndMovies.TvData;
        List<HomeMovieCardDto> movieData = tvsAndMovies.MovieData;

        // Build genre carousels with resolved items
        List<GenreCarouselData> genreCarousels = genreSourceList
            .Select(g => new GenreCarouselData(
                g.Id,
                g.Title,
                g.MoreLink,
                g.Source.Select(source => ResolveCardData(source, tvData, movieData))
                    .Where(c => c != null)
                    .Cast<CardData>()
                    .ToList()
            ))
            .Where(g => g.Items.Count > 0)
            .ToList();

        // Get random home card
        CardData? homeCardItem = genreCarousels
            .Where(g => !string.IsNullOrEmpty(g.Title))
            .SelectMany(g => g.Items)
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .Randomize()
            .FirstOrDefault();

        // Build library carousels from projection results (empty unless the
        // desktop surface asked for them).
        List<GenreCarouselData> libraryCarousels = [];

        foreach (
            (
                Library library,
                List<MovieCardDto> libraryMovies,
                List<TvCardDto> libraryShows
            ) in libraryTasks.Select(t => t.Result)
        )
        {
            bool shouldPaginate =
                (
                    library.Type == MediaTypes.MovieMediaType
                    && movieCount > UiLimits.MaximumItemsPerPage
                )
                || (
                    library.Type == MediaTypes.TvMediaType && tvCount > UiLimits.MaximumItemsPerPage
                )
                || (
                    library.Type == MediaTypes.AnimeMediaType
                    && animeCount > UiLimits.MaximumItemsPerPage
                );

            List<CardData> items = libraryMovies
                .Select(m => new CardData(m))
                .Concat(libraryShows.Select(t => new CardData(t)))
                .OrderByDescending(c => c.CreatedAt)
                .ToList();

            if (items.Count > 0)
            {
                Uri moreLink = shouldPaginate
                    ? new($"/libraries/{library.Id}/letter/A", UriKind.Relative)
                    : new Uri($"/libraries/{library.Id}", UriKind.Relative);

                libraryCarousels.Add(new(library.Id.ToString(), library.Title, moreLink, items));
            }
        }

        // "Latest in {library}" belongs to the desktop home only; the lolomo clients lay
        // their library rows out themselves, so these are duplicate content there. Same rule
        // the Index endpoint already applies — this is the /home path, which the apps call.
        //
        // Cleared rather than skipped at render: the list also supplies the prev/next ids
        // that chain the carousels together, and the code below already handles it being
        // empty. Dropping only the render loop would leave that chain pointing at rows that
        // were never emitted.
        if (version == LolomoVersion)
            libraryCarousels.Clear();

        // Build components
        List<ComponentEnvelope> components = [];

        // Home card
        if (homeCardItem != null)
        {
            components.Add(
                Component
                    .HomeCard(await BuildHeroAsync(homeCardItem, language))
                    .WithUpdate("pageLoad", "/home/card")
                    .Build()
            );
        }

        // Navigation chain: continue → genre_* → continue (circular)
        bool hasContinueWatching = continueWatching.Count > 0;
        string? continueId = hasContinueWatching ? "continue" : null;

        List<string> carouselIds =
        [
            .. libraryCarousels.Select(library => $"library_{library.Id}"),
            .. genreCarousels.Select(genre => $"genre_{genre.Id}"),
        ];
        (string? lastCarouselId, string? afterContinueId) = HomeCarouselNavigation.ForContinue(
            carouselIds
        );

        // Continue watching carousel (only when there are items to show)
        if (hasContinueWatching)
        {
            components.Add(
                Component
                    .Carousel()
                    .WithId("continue")
                    .WithNavigation(lastCarouselId, afterContinueId)
                    .WithTitle("Continue watching".Localize())
                    .WithUpdate("pageLoad", "/home/continue")
                    .WithItems(BuildContinueWatchingCards(continueWatching, country))
                    .Build()
            );
        }

        // Library "Latest in {library}" carousels (between continue and genres)
        for (int i = 0; i < libraryCarousels.Count; i++)
        {
            GenreCarouselData lib = libraryCarousels[i];

            (string? prevId, string? nextId) = HomeCarouselNavigation.ForCarousel(
                carouselIds,
                i,
                continueId
            );

            components.Add(
                Component
                    .Carousel()
                    .WithId($"library_{lib.Id}")
                    .WithNavigation(prevId, nextId)
                    .WithTitle($"Latest in {lib.Title}")
                    .WithMoreLink(lib.MoreLink)
                    .WithItems(lib.Items.Select(item => Component.Card(item).Build()))
                    .Build()
            );
        }

        // Genre carousels
        for (int i = 0; i < genreCarousels.Count; i++)
        {
            GenreCarouselData genre = genreCarousels[i];

            (string? prevId, string? nextId) = HomeCarouselNavigation.ForCarousel(
                carouselIds,
                libraryCarousels.Count + i,
                continueId
            );

            components.Add(
                Component
                    .Carousel()
                    .WithId($"genre_{genre.Id}")
                    .WithNavigation(prevId, nextId)
                    .WithTitle(genre.Title)
                    .WithMoreLink(genre.MoreLink)
                    .WithItems(genre.Items.Select(item => Component.Card(item).Build()))
                    .Build()
            );
        }

        return new() { Data = components };
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
