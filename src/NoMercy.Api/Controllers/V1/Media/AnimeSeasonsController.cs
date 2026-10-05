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
[Tags("Anime Seasons")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/anime/seasons")]
public class AnimeSeasonsController(IAnimeSeasonRepository animeSeasonRepository) : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page", "version"])]
    public async Task<IActionResult> Seasons(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        List<AnimeSeasonWithCountsDto> seasonDtos =
            await animeSeasonRepository.GetSeasonsWithCountsAsync(
                userId,
                request.Take,
                request.Page,
                ct
            );

        List<GroupCardData> seasonCards =
        [
            .. seasonDtos
                .Where(s => s.TvShowsWithVideo > 0 || s.MoviesWithVideo > 0)
                .Select(dto => new GroupCardData(dto)),
        ];

        if (request.Version != "lolomo")
        {
            ComponentEnvelope response = Component
                .Grid()
                .WithId("anime-seasons")
                .WithItems(seasonCards.Select(card => Component.GroupCard().WithData(card)));

            return Ok(ComponentResponse.From(response));
        }

        // One row per year, newest first; the sort key keeps the quarters in season order.
        string[] years =
        [
            .. seasonCards
                .Select(card => card.Year ?? 0)
                .Distinct()
                .OrderDescending()
                .Select(year => year.ToString("D4")),
        ];

        List<ComponentEnvelope> components = new();

        foreach (string year in years)
        {
            int index = Array.IndexOf(years, year);

            List<GroupCardData> carouselItems = seasonCards
                .Where(card => (card.Year ?? 0).ToString("D4") == year)
                .OrderBy(card => card.TitleSort)
                .ToList();

            components.Add(
                Component
                    .Carousel()
                    .WithId(year)
                    .WithTitle(year)
                    .WithNavigation(
                        index == 0 ? null : years[index - 1],
                        index == years.Length - 1 ? null : years[index + 1]
                    )
                    .WithItems(carouselItems.Select(card => Component.GroupCard().WithData(card)))
            );
        }

        return Ok(new ComponentResponse { Data = components });
    }

    [HttpGet]
    [Route("{seasonId}")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
    public async Task<IActionResult> Season(
        int seasonId,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        (
            AnimeSeasonDetailDto? seasonDetail,
            List<HomeMovieCardDto> movies,
            List<HomeTvCardDto> tvShows
        ) = await animeSeasonRepository.GetSeasonCardsAsync(
            userId,
            seasonId,
            language,
            country,
            request.Take,
            request.Page,
            ct
        );

        if (seasonDetail is null || (movies.Count == 0 && tvShows.Count == 0))
            return NotFoundResponse("Anime season not found");

        ComponentEnvelope response = TitleCardGrid("anime-season-items", movies, tvShows);

        return Ok(ComponentResponse.From(response));
    }
}
