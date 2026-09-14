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
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media Genres")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/genres")]
public class GenresController : BaseController
{
    private readonly IGenreRepository _genreRepository;

    public GenresController(IGenreRepository genreRepository)
    {
        _genreRepository = genreRepository;
    }

    [HttpGet]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
    public async Task<IActionResult> Genres(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();

        // Use optimized query that computes counts in database
        List<GenreWithCountsDto> genreDtos = await _genreRepository.GetGenresWithCountsAsync(
            userId,
            language,
            request.Take,
            request.Page
        );

        // Create cards for each genre
        List<GenreCardData> genreCards =
        [
            .. genreDtos
                .Where(g => g.TvShowsWithVideo > 0 || g.MoviesWithVideo > 0)
                .Select(dto => new GenreCardData(dto)),
        ];

        ComponentEnvelope response = Component
            .Grid()
            .WithId("genres")
            .WithItems(genreCards.Select(card => Component.GenreCard().WithData(card)));

        return Ok(ComponentResponse.From(response));
    }

    [HttpGet]
    [Route("{genreId}")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page", "version"])]
    public async Task<IActionResult> Genre(
        int genreId,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        (GenreDetailDto? genreDetail, List<HomeMovieCardDto> movies, List<HomeTvCardDto> tvShows) =
            await _genreRepository.GetGenreCardsAsync(
                userId,
                genreId,
                language,
                country,
                request.Take,
                request.Page,
                ct
            );

        if (genreDetail is null || (movies.Count == 0 && tvShows.Count == 0))
            return NotFoundResponse("Genre not found");

        if (request.Version != "lolomo")
        {
            // Simple grid view
            ComponentEnvelope response = TitleCardGrid("genre-items", movies, tvShows, country);

            return Ok(ComponentResponse.From(response));
        }

        // Carousel view organized by first letter
        List<ComponentEnvelope> carousels =
        [
            .. Letters
                .Select(
                    (letter, index) =>
                    {
                        List<CardData> carouselItems =
                        [
                            .. movies
                                .Select(movie => new CardData(movie, country))
                                .Where(card => AlphaBucket.Matches(card.TitleSort, letter))
                                .Concat(
                                    tvShows
                                        .Select(tv => new CardData(tv, country))
                                        .Where(card => AlphaBucket.Matches(card.TitleSort, letter))
                                )
                                .OrderBy(card => card.TitleSort),
                        ];

                        if (carouselItems.Count == 0)
                            return null;

                        return Component
                            .Carousel()
                            .WithId(letter)
                            .WithTitle(letter)
                            .WithNavigation(
                                index == 0 ? null : Letters.ElementAtOrDefault(index - 1) ?? null,
                                index == Letters.Length - 1
                                    ? null
                                    : Letters.ElementAtOrDefault(index + 1) ?? null
                            )
                            .WithItems(
                                carouselItems.Select(card => Component.Card().WithData(card))
                            )
                            .Build();
                    }
                )
                .Where(c => c != null)
                .Cast<ComponentEnvelope>(),
        ];

        ComponentEnvelope containerResponse = Component
            .Container()
            .WithId("genre-carousels")
            .WithItems(carousels);

        return Ok(ComponentResponse.From(containerResponse));
    }
}
