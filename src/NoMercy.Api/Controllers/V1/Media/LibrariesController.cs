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

        // Start all independent queries in parallel; each repository call opens its own context.
        Task<List<Library>> librariesTask = libraryRepository.GetLibrariesLite(userId, ct);
        Task<Dictionary<Ulid, int>> countsTask = libraryRepository.GetLibraryItemCountsAsync(
            userId,
            ct
        );
        Task<List<CollectionListDto>> collectionsTask =
            collectionRepository.GetCollectionItemCardsAsync(userId, language, country, 10, 0, ct);
        Task<List<SpecialCardDto>> specialsTask = specialRepository.GetSpecialItemCardsAsync(
            userId,
            language,
            country,
            10,
            0,
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

        await Task.WhenAll([
            librariesTask,
            countsTask,
            collectionsTask,
            specialsTask,
            randomTvTask,
            randomMovieTask,
            favoritesTask,
            myListsTask,
        ]);

        List<Library> libraries = librariesTask.Result;
        Dictionary<Ulid, int> itemCounts = countsTask.Result;
        List<CollectionListDto> collections = collectionsTask.Result;
        List<SpecialCardDto> specials = specialsTask.Result;
        HomeTvCardDto? tv = randomTvTask.Result;
        HomeMovieCardDto? movie = randomMovieTask.Result;
        FavoritesData favorites = favoritesTask.Result;
        List<UserPlaylistSummary> myLists = myListsTask.Result;

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
        favoriteCards = favoriteCards
            .OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(card => card.Link)
            .ToList();

        List<NmCardDto> myListCards = myLists
            .Select(summary => new NmCardDto
            {
                Id = summary.Id,
                Title = summary.Name,
                Poster = summary.Cover,
                Link = new($"/lists/{summary.Id}", UriKind.Relative),
                Type = "playlist",
                NumberOfItems = summary.ItemCount,
                HaveItems = summary.ItemCount,
            })
            .ToList();

        // Fetch library data in parallel; each repository call opens its own context.
        Library[] nonMusicLibraries = libraries.Where(lib => lib.Type != "music").ToArray();

        Task<(
            Library library,
            List<MovieCardDto> movies,
            List<TvCardDto> shows
        )>[] libraryDataTasks = nonMusicLibraries
            .Select(async library =>
            {
                List<MovieCardDto> movies = await libraryRepository.GetLibraryMovieCardsAsync(
                    userId,
                    library.Id,
                    country,
                    10,
                    0,
                    ct
                );
                List<TvCardDto> shows = await libraryRepository.GetLibraryTvCardsAsync(
                    userId,
                    library.Id,
                    country,
                    10,
                    0,
                    ct
                );
                return (library, movies, shows);
            })
            .ToArray();

        (Library library, List<MovieCardDto> movies, List<TvCardDto> shows)[] libraryDataResults =
            await Task.WhenAll(libraryDataTasks);

        List<NmCarouselDto<NmCardDto>> list = [];

        foreach (
            (
                Library library,
                List<MovieCardDto> libraryMovies,
                List<TvCardDto> libraryShows
            ) in libraryDataResults
        )
        {
            int totalItems = itemCounts.GetValueOrDefault(library.Id);
            Uri moreLink =
                totalItems > 500
                    ? new($"/libraries/{library.Id}/letter/A", UriKind.Relative)
                    : new($"/libraries/{library.Id}", UriKind.Relative);

            list.Add(
                new()
                {
                    Title = library.Title,
                    MoreLink = moreLink,
                    Items =
                    [
                        .. libraryMovies.Select(m => new NmCardDto(m, country)),
                        .. libraryShows.Select(t => new NmCardDto(t, country)),
                    ],
                }
            );
        }

        list.Add(
            new()
            {
                Title = "Favorites",
                MoreLink = new("/favorites", UriKind.Relative),
                Items = favoriteCards,
            }
        );

        list.Add(
            new()
            {
                Title = "My Lists",
                MoreLink = new("/lists", UriKind.Relative),
                Items = myListCards,
            }
        );

        list.Add(
            new()
            {
                Title = "Collections",
                MoreLink = new("/collection", UriKind.Relative),
                Items = collections
                    .Select(collection => new NmCardDto(collection, country))
                    .ToList(),
            }
        );

        list.Add(
            new()
            {
                Title = "Specials",
                MoreLink = new("/specials", UriKind.Relative),
                Items = specials.Select(special => new NmCardDto(special, country)).ToList(),
            }
        );

        List<NmCardDto> genres = [];
        if (tv != null)
            genres.Add(new(tv, country));

        if (movie != null)
            genres.Add(new(movie, country));

        NmCardDto? homeCardItem = genres
            .Where(g => !string.IsNullOrWhiteSpace(g.Title))
            .Randomize()
            .FirstOrDefault();

        List<ComponentEnvelope> components = new();

        // Add home card
        if (homeCardItem != null)
        {
            HomeCardData homeCardData = new(homeCardItem);
            dynamic? homeCard = Component
                .HomeCard()
                .WithId("home_card")
                .WithTitle(homeCardData.Title)
                .WithData(homeCardData)
                .WithUpdate("pageLoad", "/home/card")
                .Build();

            components.Add(homeCard);
        }

        // Add carousels for each library
        for (int index = 0; index < list.Count; index++)
        {
            NmCarouselDto<NmCardDto> carouselData = list[index];
            ComponentEnvelope carousel = Component
                .Carousel()
                .WithId($"library_{carouselData.Id}")
                .WithTitle(carouselData.Title)
                .WithMoreLink(carouselData.MoreLink)
                .WithNavigation(
                    index == 0 ? "home_card" : $"library_{list[index - 1].Id}",
                    index == list.Count - 1 ? null : $"library_{list[index + 1].Id}"
                )
                .WithItems(carouselData.Items.Select(item => Component.Card().WithData(new(item))));

            components.Add(carousel);
        }

        return Ok(ComponentResponse.From(components));
    }

    [HttpGet]
    [Route("tv")]
    public async Task<IActionResult> Tv(CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        // Start all independent queries in parallel; each repository call opens its own context.
        Task<List<Library>> librariesTask = libraryRepository.GetLibrariesLite(userId, ct);
        Task<List<CollectionListDto>> collectionsTask =
            collectionRepository.GetCollectionItemCardsAsync(userId, language, country, 6, 0, ct);
        Task<List<SpecialCardDto>> specialsTask = specialRepository.GetSpecialItemCardsAsync(
            userId,
            language,
            country,
            6,
            0,
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

        await Task.WhenAll([
            librariesTask,
            collectionsTask,
            specialsTask,
            randomTvTask,
            randomMovieTask,
            favoritesTask,
            myListsTask,
        ]);

        List<Library> libraries = librariesTask.Result;
        List<CollectionListDto> collections = collectionsTask.Result;
        List<SpecialCardDto> specials = specialsTask.Result;
        HomeTvCardDto? tv = randomTvTask.Result;
        HomeMovieCardDto? movie = randomMovieTask.Result;
        FavoritesData favorites = favoritesTask.Result;
        List<UserPlaylistSummary> myLists = myListsTask.Result;

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
        favoriteCards = favoriteCards
            .OrderBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
            .DistinctBy(card => card.Link)
            .ToList();

        List<NmCardDto> myListCards = myLists
            .Select(summary => new NmCardDto
            {
                Id = summary.Id,
                Title = summary.Name,
                Poster = summary.Cover,
                Link = new($"/lists/{summary.Id}", UriKind.Relative),
                Type = "playlist",
                NumberOfItems = summary.ItemCount,
                HaveItems = summary.ItemCount,
            })
            .ToList();

        // Fetch library data in parallel; each repository call opens its own context.
        Task<(
            Library library,
            List<MovieCardDto> movies,
            List<TvCardDto> shows
        )>[] libraryDataTasks = libraries
            .Select(async library =>
            {
                List<MovieCardDto> movies = await libraryRepository.GetLibraryMovieCardsAsync(
                    userId,
                    library.Id,
                    country,
                    6,
                    0,
                    ct
                );
                List<TvCardDto> shows = await libraryRepository.GetLibraryTvCardsAsync(
                    userId,
                    library.Id,
                    country,
                    6,
                    0,
                    ct
                );
                return (library, movies, shows);
            })
            .ToArray();

        (Library library, List<MovieCardDto> movies, List<TvCardDto> shows)[] libraryDataResults =
            await Task.WhenAll(libraryDataTasks);

        List<NmCarouselDto<NmCardDto>> list = [];

        foreach (
            (
                Library library,
                List<MovieCardDto> libraryMovies,
                List<TvCardDto> libraryShows
            ) in libraryDataResults
        )
        {
            list.Add(
                new()
                {
                    Id = "library_" + library.Id,
                    Title = library.Title,
                    MoreLink = new($"/libraries/{library.Id}", UriKind.Relative),
                    Items = libraryMovies
                        .Select(m => new NmCardDto(m, country))
                        .Concat(libraryShows.Select(t => new NmCardDto(t, country)))
                        .ToList(),
                }
            );
        }

        list.Add(
            new()
            {
                Id = "library_favorites",
                Title = "Favorites",
                MoreLink = new("/favorites", UriKind.Relative),
                Items = favoriteCards,
            }
        );

        list.Add(
            new()
            {
                Id = "library_lists",
                Title = "My Lists",
                MoreLink = new("/lists", UriKind.Relative),
                Items = myListCards,
            }
        );

        list.Add(
            new()
            {
                Id = "library_collections",
                Title = "Collections",
                MoreLink = new("/collection", UriKind.Relative),
                Items = collections
                    .Select(collection => new NmCardDto(collection, country))
                    .ToList(),
            }
        );

        list.Add(
            new()
            {
                Id = "library_specials",
                Title = "Specials",
                MoreLink = new("/specials", UriKind.Relative),
                Items = specials.Select(special => new NmCardDto(special, country)).ToList(),
            }
        );

        List<NmCardDto> genres = [];
        if (tv != null)
            genres.Add(new(tv, country));

        if (movie != null)
            genres.Add(new(movie, country));

        List<ComponentEnvelope> components = new();

        // Add carousels for each library
        for (int index = 0; index < list.Count; index++)
        {
            NmCarouselDto<NmCardDto> carouselData = list[index];
            dynamic? carousel = Component
                .Carousel()
                .WithId(carouselData.Id)
                .WithTitle(carouselData.Title)
                .WithMoreLink(carouselData.MoreLink)
                .WithNavigation(
                    index == 0 ? "home_card" : list[index - 1].Id,
                    index == list.Count - 1 ? null : list[index + 1].Id
                )
                .WithItems(
                    carouselData.Items.Take(6).Select(item => Component.Card().WithData(new(item)))
                );
            components.Add(carousel);
        }

        return Ok(ComponentResponse.From(components));
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
                .Select(movie => new CardData(movie, country))
                .Concat(libraryShows.Select(tv => new CardData(tv, country)))
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
                .Select(movie => new CardData(movie, country))
                .Where(collection => AlphaBucket.Matches(collection.TitleSort, letter))
                .Concat(
                    libraryShows
                        .Select(tv => new CardData(tv, country))
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

        ComponentEnvelope response = TitleCardGrid(
                $"library-{libraryId}-{letter}",
                movies,
                shows,
                country
            )
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
