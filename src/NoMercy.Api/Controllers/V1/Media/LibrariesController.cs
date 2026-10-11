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

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media Libraries")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/libraries")]
public class LibrariesController(
    ILibraryRepository libraryRepository,
    ICollectionRepository collectionRepository,
    ISpecialRepository specialRepository,
    IHomeRepository homeRepository,
    IUserPlaylistRepository userPlaylistRepository,
    IAnimeThemeRepository animeThemeRepository,
    IAnimeDemographicRepository animeDemographicRepository,
    IAnimeSeasonRepository animeSeasonRepository,
    IServerConfigurationRepository serverConfigurationRepository,
    TimeProvider timeProvider
) : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 300)]
    public async Task<IActionResult> Libraries(CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        List<LibrariesResponseItemDto> response = (await libraryRepository.GetLibraries(userId, ct))
            .Select(library => new LibrariesResponseItemDto(library))
            .ToList();

        return Ok(new LibrariesDto { Data = response.OrderBy(library => library.Order) });
    }

    [HttpGet]
    [Route("mobile")]
    public async Task<IActionResult> Mobile(CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        // Every repository call opens its own context, so all of these run in parallel.
        Task<LibraryOverview> overviewTask = LoadOverviewAsync(
            userId,
            language,
            country,
            take: 10,
            library => library.Type != "music",
            ct
        );
        Task<Dictionary<Ulid, int>> countsTask = libraryRepository.GetLibraryItemCountsAsync(
            userId,
            ct
        );
        Task<HomeTvCardDto?> randomTvTask = libraryRepository.GetRandomTvCardAsync(
            userId,
            language,
            country,
            ct
        );
        Task<HomeMovieCardDto?> randomMovieTask = libraryRepository.GetRandomMovieCardAsync(
            userId,
            language,
            country,
            ct
        );

        await Task.WhenAll(overviewTask, countsTask, randomTvTask, randomMovieTask);

        LibraryOverview overview = overviewTask.Result;
        Dictionary<Ulid, int> itemCounts = countsTask.Result;

        // Mobile carousel ids stay "library_" as they were; only the anime rows have their own.
        List<CarouselRow> rows =
        [
            .. overview.Libraries.Select(entry =>
                CardRow(
                    new()
                    {
                        Title = entry.Library.Title,
                        MoreLink =
                            itemCounts.GetValueOrDefault(entry.Library.Id) > 500
                                ? new($"/libraries/{entry.Library.Id}/letter/A", UriKind.Relative)
                                : new($"/libraries/{entry.Library.Id}", UriKind.Relative),
                        Items = entry.Cards,
                    },
                    take: null
                )
            ),
            .. await AnimeCarouselsAsync(userId, language, overview, take: 10, ct),
            .. SectionCarousels(overview, withIds: false).Select(section => CardRow(section, null)),
        ];

        List<NmCardDto> genres = [];
        if (randomTvTask.Result is { } tv)
            genres.Add(new(tv));
        if (randomMovieTask.Result is { } movie)
            genres.Add(new(movie));

        NmCardDto? homeCardItem = genres
            .Where(g => !string.IsNullOrWhiteSpace(g.Title))
            .Randomize()
            .FirstOrDefault();

        List<ComponentEnvelope> components = [];

        if (homeCardItem != null)
        {
            HomeCardData homeCardData = new(homeCardItem);
            components.Add(
                Component
                    .HomeCard()
                    .WithId("home_card")
                    .WithTitle(homeCardData.Title)
                    .WithData(homeCardData)
                    .WithUpdate("pageLoad", "/home/card")
                    .Build()
            );
        }

        components.AddRange(Chain(rows));

        return Ok(ComponentResponse.From(components));
    }

    [HttpGet]
    [Route("tv")]
    public async Task<IActionResult> Tv(CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        string language = Language();

        LibraryOverview overview = await LoadOverviewAsync(
            userId,
            language,
            Country(),
            take: 6,
            _ => true,
            ct
        );

        List<CarouselRow> rows =
        [
            .. overview.Libraries.Select(entry =>
                CardRow(
                    new()
                    {
                        Id = "library_" + entry.Library.Id,
                        Title = entry.Library.Title,
                        MoreLink = new($"/libraries/{entry.Library.Id}", UriKind.Relative),
                        Items = entry.Cards,
                    },
                    take: 6
                )
            ),
            .. await AnimeCarouselsAsync(userId, language, overview, take: 6, ct),
            .. SectionCarousels(overview, withIds: true).Select(section => CardRow(section, 6)),
        ];

        return Ok(ComponentResponse.From(LolomoPage("libraries", Chain(rows))));
    }

    /// <summary>One carousel of the overview: its id, title, show-all link and cards.</summary>
    private sealed record CarouselRow(
        string Id,
        string Title,
        Uri? MoreLink,
        IEnumerable<IComponentBuilder> Items
    );

    /// <param name="take">The most cards the row shows; null shows every card it has.</param>
    private static CarouselRow CardRow(NmCarouselDto<NmCardDto> carousel, int? take) =>
        new(
            carousel.Id ?? "library_",
            carousel.Title,
            carousel.MoreLink,
            (take is { } count ? carousel.Items.Take(count) : carousel.Items).Select(item =>
                Component.Card().WithData(new(item))
            )
        );

    /// <summary>
    /// The rows as carousels, each linked to the row before and after it, so a TV remote
    /// moves through them in order. The first row goes back to the home card.
    /// </summary>
    private static IEnumerable<ComponentEnvelope> Chain(List<CarouselRow> rows) =>
        rows.Select(
            (row, index) =>
                Component
                    .Carousel()
                    .WithId(row.Id)
                    .WithTitle(row.Title)
                    .WithMoreLink(row.MoreLink)
                    .WithNavigation(
                        index == 0 ? "home_card" : rows[index - 1].Id,
                        index == rows.Count - 1 ? null : rows[index + 1].Id
                    )
                    .WithItems(row.Items)
                    .Build()
        );

    /// <summary>
    /// The anime themes, demographics and seasons as rows of group cards, so a phone or TV
    /// can reach those pages. None when no video library holds a themed title, and a group
    /// with no title to play is left out. The order of each row is a server setting.
    /// </summary>
    private async Task<List<CarouselRow>> AnimeCarouselsAsync(
        Guid userId,
        string language,
        LibraryOverview overview,
        int take,
        CancellationToken ct
    )
    {
        if (
            !LibraryNavigation.HasVideo(overview.Libraries.Select(entry => entry.Library))
            || !await animeThemeRepository.AnyThemedTitlesAsync(ct)
        )
            return [];

        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // The anime repositories share one scoped context, so the rows load one after another.
        List<int> themeIds = await animeThemeRepository.GetTopThemeIdsAsync(
            userId,
            await OrderAsync(
                ServerConfigurationKeys.AnimeCarouselOrderThemes,
                AnimeGroupKind.Theme,
                ct
            ),
            today,
            take,
            ct
        );
        List<GroupCardData> themes =
        [
            .. (
                await animeThemeRepository.GetThemesWithCountsByIdsAsync(
                    userId,
                    language,
                    themeIds,
                    ct
                )
            )
                .Where(theme => theme.TvShowsWithVideo > 0 || theme.MoviesWithVideo > 0)
                .Select(theme => new GroupCardData(theme)),
        ];

        List<int> demographicIds = await animeDemographicRepository.GetTopDemographicIdsAsync(
            userId,
            await OrderAsync(
                ServerConfigurationKeys.AnimeCarouselOrderDemographics,
                AnimeGroupKind.Demographic,
                ct
            ),
            today,
            take,
            ct
        );
        List<GroupCardData> demographics =
        [
            .. (
                await animeDemographicRepository.GetDemographicsWithCountsByIdsAsync(
                    userId,
                    language,
                    demographicIds,
                    ct
                )
            )
                .Where(demographic =>
                    demographic.TvShowsWithVideo > 0 || demographic.MoviesWithVideo > 0
                )
                .Select(demographic => new GroupCardData(demographic)),
        ];

        List<int> seasonIds = await animeSeasonRepository.GetTopSeasonIdsAsync(
            userId,
            await OrderAsync(
                ServerConfigurationKeys.AnimeCarouselOrderSeasons,
                AnimeGroupKind.Season,
                ct
            ),
            today,
            take,
            ct
        );
        List<GroupCardData> seasons =
        [
            .. (await animeSeasonRepository.GetSeasonsWithCountsByIdsAsync(userId, seasonIds, ct))
                .Where(season => season.TvShowsWithVideo > 0 || season.MoviesWithVideo > 0)
                .Select(season => new GroupCardData(season)),
        ];

        return
        [
            .. new (string Id, string Title, string Link, List<GroupCardData> Cards)[]
            {
                ("anime_themes", "Anime Themes", "/anime/themes", themes),
                ("anime_demographics", "Anime Demographics", "/anime/demographics", demographics),
                ("anime_seasons", "Anime Seasons", "/anime/seasons", seasons),
            }
                .Where(group => group.Cards.Count > 0)
                .Select(group => new CarouselRow(
                    group.Id,
                    group.Title,
                    new(group.Link, UriKind.Relative),
                    group.Cards.Select(card => Component.GroupCard().WithData(card))
                )),
        ];
    }

    private async Task<AnimeCarouselOrder> OrderAsync(
        string key,
        AnimeGroupKind kind,
        CancellationToken ct
    ) =>
        AnimeCarouselOrderParser.Parse(
            await serverConfigurationRepository.GetValueAsync(key, ct),
            kind
        );

    private sealed record LibraryCards(Library Library, List<NmCardDto> Cards);

    private sealed record LibraryOverview(
        LibraryCards[] Libraries,
        List<NmCardDto> Favorites,
        List<NmCardDto> MyLists,
        List<NmCardDto> Collections,
        List<NmCardDto> Specials
    );

    /// <summary>
    /// The cards the library overview shows: <paramref name="take"/> titles per library,
    /// then favorites, the user's lists, collections and specials.
    /// </summary>
    private async Task<LibraryOverview> LoadOverviewAsync(
        Guid userId,
        string language,
        string country,
        int take,
        Func<Library, bool> includeLibrary,
        CancellationToken ct
    )
    {
        Task<List<Library>> librariesTask = libraryRepository.GetLibrariesLite(userId, ct);
        Task<List<CollectionListDto>> collectionsTask =
            collectionRepository.GetCollectionItemCardsAsync(
                userId,
                language,
                country,
                take,
                0,
                ct
            );
        Task<List<SpecialCardDto>> specialsTask = specialRepository.GetSpecialItemCardsAsync(
            userId,
            language,
            country,
            take,
            0,
            ct
        );
        Task<FavoritesData> favoritesTask = homeRepository.GetFavoritesAsync(
            userId,
            language,
            country,
            ct
        );
        Task<List<UserPlaylistSummary>> myListsTask = userPlaylistRepository.GetUserPlaylistsAsync(
            userId,
            ct
        );

        await Task.WhenAll(
            librariesTask,
            collectionsTask,
            specialsTask,
            favoritesTask,
            myListsTask
        );

        LibraryCards[] libraries = await Task.WhenAll(
            librariesTask
                .Result.Where(includeLibrary)
                .Select(async library =>
                {
                    List<MovieCardDto> movies = await libraryRepository.GetLibraryMovieCardsAsync(
                        userId,
                        library.Id,
                        country,
                        take,
                        0,
                        ct
                    );
                    List<TvCardDto> shows = await libraryRepository.GetLibraryTvCardsAsync(
                        userId,
                        library.Id,
                        country,
                        take,
                        0,
                        ct
                    );
                    return new LibraryCards(
                        library,
                        [
                            .. movies.Select(m => new NmCardDto(m)),
                            .. shows.Select(t => new NmCardDto(t)),
                        ]
                    );
                })
        );

        FavoritesData favorites = favoritesTask.Result;
        List<NmCardDto> favoriteCards =
        [
            .. favorites.Movies.Select(favoriteMovie => new NmCardDto(favoriteMovie, country)),
            .. favorites.TvShows.Select(favoriteTv => new NmCardDto(favoriteTv, country)),
            .. favorites.Collections.Select(favoriteCollection => new NmCardDto(
                favoriteCollection,
                country
            )),
            .. favorites.Specials.Select(favoriteSpecial => new NmCardDto(
                favoriteSpecial,
                country
            )),
        ];

        return new(
            libraries,
            [
                .. favoriteCards
                    .OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
                    .DistinctBy(card => card.Link),
            ],
            [
                .. myListsTask.Result.Select(summary => new NmCardDto
                {
                    Id = summary.Id,
                    Title = summary.Name,
                    Poster = summary.Cover,
                    Link = new($"/lists/{summary.Id}", UriKind.Relative),
                    Type = "playlist",
                    NumberOfItems = summary.ItemCount,
                    HaveItems = summary.ItemCount,
                }),
            ],
            [.. collectionsTask.Result.Select(collection => new NmCardDto(collection))],
            [.. specialsTask.Result.Select(special => new NmCardDto(special))]
        );
    }

    private static IEnumerable<NmCarouselDto<NmCardDto>> SectionCarousels(
        LibraryOverview overview,
        bool withIds
    )
    {
        yield return Section("favorites", "Favorites", "/favorites", overview.Favorites, withIds);
        yield return Section("lists", "My Lists", "/lists", overview.MyLists, withIds);
        yield return Section(
            "collections",
            "Collections",
            "/collection",
            overview.Collections,
            withIds
        );
        yield return Section("specials", "Specials", "/specials", overview.Specials, withIds);
    }

    private static NmCarouselDto<NmCardDto> Section(
        string key,
        string title,
        string link,
        List<NmCardDto> items,
        bool withId
    )
    {
        NmCarouselDto<NmCardDto> carousel = new()
        {
            Title = title,
            MoreLink = new(link, UriKind.Relative),
            Items = items,
        };
        if (withId)
            carousel.Id = "library_" + key;
        return carousel;
    }

    [HttpGet]
    [Route("{libraryId:ulid}")]
    public async Task<IActionResult> Library(
        Ulid libraryId,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        // Fetch movies and shows in parallel; each repository call opens its own context.
        Task<List<MovieCardDto>> moviesTask = libraryRepository.GetLibraryMovieCardsAsync(
            userId,
            libraryId,
            country,
            request.Take,
            request.Page * request.Take,
            ct
        );
        Task<List<TvCardDto>> showsTask = libraryRepository.GetLibraryTvCardsAsync(
            userId,
            libraryId,
            country,
            request.Take,
            request.Page * request.Take,
            ct
        );

        await Task.WhenAll([moviesTask, showsTask]);

        List<MovieCardDto> libraryMovies = moviesTask.Result;
        List<TvCardDto> libraryShows = showsTask.Result;

        if (request.Version != "lolomo")
        {
            List<CardData> cardItems = libraryMovies
                .Select(movie => new CardData(movie))
                .Concat(libraryShows.Select(tv => new CardData(tv)))
                .OrderBy(item => item.TitleSort)
                .ToList();

            ComponentEnvelope response = Component
                .Grid()
                .WithId($"library-{libraryId}")
                .WithItems(cardItems.Select(item => Component.Card().WithData(item)));

            return Ok(ComponentResponse.From(response));
        }
        List<ComponentEnvelope> components = new();

        foreach (string letter in Letters)
        {
            int index = Array.IndexOf(Letters, letter);

            List<CardData> carouselItems = libraryMovies
                .Select(movie => new CardData(movie))
                .Where(collection => AlphaBucket.Matches(collection.TitleSort, letter))
                .Concat(
                    libraryShows
                        .Select(tv => new CardData(tv))
                        .Where(collection => AlphaBucket.Matches(collection.TitleSort, letter))
                )
                .OrderBy(item => item.TitleSort)
                .ToList();

            if (carouselItems.Count == 0)
                continue;

            components.Add(
                Component
                    .Carousel()
                    .WithId(letter)
                    .WithTitle(letter)
                    .WithNavigation(
                        index == 0 ? null : Letters.ElementAtOrDefault(index - 1) ?? null,
                        index == Letters.Length - 1
                            ? null
                            : Letters.ElementAtOrDefault(index + 1) ?? null
                    )
                    .WithItems(carouselItems.Select(item => Component.Card().WithData(item)))
            );
        }

        return Ok(ComponentResponse.From(LolomoPage($"library-{libraryId}", components)));
    }

    [HttpGet]
    [Route("{libraryId:ulid}/letter/{letter}")]
    public async Task<IActionResult> LibraryByLetter(
        Ulid libraryId,
        string letter,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        // Fetch movies and shows in parallel; each repository call opens its own context.
        Task<List<HomeMovieCardDto>> moviesTask =
            libraryRepository.GetPaginatedLibraryMovieCardsAsync(
                userId,
                libraryId,
                letter,
                language,
                country,
                request.Take,
                request.Page,
                ct
            );
        Task<List<HomeTvCardDto>> showsTask = libraryRepository.GetPaginatedLibraryTvCardsAsync(
            userId,
            libraryId,
            letter,
            language,
            country,
            request.Take,
            request.Page,
            ct
        );

        await Task.WhenAll([moviesTask, showsTask]);

        List<HomeMovieCardDto> movies = moviesTask.Result;
        List<HomeTvCardDto> shows = showsTask.Result;

        ComponentEnvelope response = TitleCardGrid($"library-{libraryId}-{letter}", movies, shows)
            .WithTitle(letter);

        return Ok(ComponentResponse.From(response));
    }

    /// Dead-letter review: media items that failed to import after all retries.
    /// Pass ?resolved=false to see only outstanding failures.
    [HttpGet]
    [Route("{libraryId}/import-failures")]
    public async Task<IActionResult> ImportFailures(
        Ulid libraryId,
        [FromQuery] bool? resolved = null,
        CancellationToken ct = default
    )
    {
        List<ImportFailure> failures = await libraryRepository.GetImportFailuresAsync(
            libraryId,
            resolved,
            ct
        );

        return Ok(new { data = failures });
    }
}
