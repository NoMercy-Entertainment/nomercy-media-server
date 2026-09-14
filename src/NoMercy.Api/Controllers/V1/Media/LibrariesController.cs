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
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database;
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
    IUserPlaylistRepository userPlaylistRepository
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

        List<NmCarouselDto<NmCardDto>> list =
        [
            .. overview.Libraries.Select(entry => new NmCarouselDto<NmCardDto>
            {
                Title = entry.Library.Title,
                MoreLink =
                    itemCounts.GetValueOrDefault(entry.Library.Id) > 500
                        ? new($"/libraries/{entry.Library.Id}/letter/A", UriKind.Relative)
                        : new($"/libraries/{entry.Library.Id}", UriKind.Relative),
                Items = entry.Cards,
            }),
            .. SectionCarousels(overview, withIds: false),
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

        for (int index = 0; index < list.Count; index++)
        {
            NmCarouselDto<NmCardDto> carouselData = list[index];
            components.Add(
                Component
                    .Carousel()
                    .WithId($"library_{carouselData.Id}")
                    .WithTitle(carouselData.Title)
                    .WithMoreLink(carouselData.MoreLink)
                    .WithNavigation(
                        index == 0 ? "home_card" : $"library_{list[index - 1].Id}",
                        index == list.Count - 1 ? null : $"library_{list[index + 1].Id}"
                    )
                    .WithItems(
                        carouselData.Items.Select(item => Component.Card().WithData(new(item)))
                    )
            );
        }

        return Ok(ComponentResponse.From(components));
    }

    [HttpGet]
    [Route("tv")]
    public async Task<IActionResult> Tv(CancellationToken ct = default)
    {
        LibraryOverview overview = await LoadOverviewAsync(
            User.UserId(),
            Language(),
            Country(),
            take: 6,
            _ => true,
            ct
        );

        List<NmCarouselDto<NmCardDto>> list =
        [
            .. overview.Libraries.Select(entry => new NmCarouselDto<NmCardDto>
            {
                Id = "library_" + entry.Library.Id,
                Title = entry.Library.Title,
                MoreLink = new($"/libraries/{entry.Library.Id}", UriKind.Relative),
                Items = entry.Cards,
            }),
            .. SectionCarousels(overview, withIds: true),
        ];

        List<ComponentEnvelope> components = [];

        for (int index = 0; index < list.Count; index++)
        {
            NmCarouselDto<NmCardDto> carouselData = list[index];
            components.Add(
                Component
                    .Carousel()
                    .WithId(carouselData.Id)
                    .WithTitle(carouselData.Title)
                    .WithMoreLink(carouselData.MoreLink)
                    .WithNavigation(
                        index == 0 ? "home_card" : list[index - 1].Id,
                        index == list.Count - 1 ? null : list[index + 1].Id
                    )
                    .WithItems(
                        carouselData
                            .Items.Take(6)
                            .Select(item => Component.Card().WithData(new(item)))
                    )
            );
        }

        return Ok(ComponentResponse.From(components));
    }

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

        return Ok(new ComponentResponse { Data = components });
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
