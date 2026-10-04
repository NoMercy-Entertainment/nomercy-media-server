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
[Tags("Anime Themes")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/anime/themes")]
public class AnimeThemesController(IAnimeThemeRepository animeThemeRepository) : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page", "version"])]
    public async Task<IActionResult> Themes(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        string language = Language();

        List<AnimeThemeWithCountsDto> themeDtos =
            await animeThemeRepository.GetThemesWithCountsAsync(
                userId,
                language,
                request.Take,
                request.Page,
                ct
            );

        List<GroupCardData> themeCards =
        [
            .. themeDtos
                .Where(t => t.TvShowsWithVideo > 0 || t.MoviesWithVideo > 0)
                .Select(dto => new GroupCardData(dto)),
        ];

        if (request.Version != "lolomo")
        {
            ComponentEnvelope response = Component
                .Grid()
                .WithId("anime-themes")
                .WithItems(themeCards.Select(card => Component.GroupCard().WithData(card)));

            return Ok(ComponentResponse.From(response));
        }

        List<ComponentEnvelope> components = new();

        foreach (string letter in Letters)
        {
            int index = Array.IndexOf(Letters, letter);

            List<GroupCardData> carouselItems = themeCards
                .Where(card => AlphaBucket.Matches(card.TitleSort, letter))
                .OrderBy(card => card.TitleSort)
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
                    .WithItems(carouselItems.Select(card => Component.GroupCard().WithData(card)))
            );
        }

        return Ok(new ComponentResponse { Data = components });
    }

    [HttpGet]
    [Route("{themeId}")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
    public async Task<IActionResult> Theme(
        int themeId,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        (
            AnimeThemeDetailDto? themeDetail,
            List<HomeMovieCardDto> movies,
            List<HomeTvCardDto> tvShows
        ) = await animeThemeRepository.GetThemeCardsAsync(
            userId,
            themeId,
            language,
            country,
            request.Take,
            request.Page,
            ct
        );

        if (themeDetail is null || (movies.Count == 0 && tvShows.Count == 0))
            return NotFoundResponse("Anime theme not found");

        ComponentEnvelope response = TitleCardGrid("anime-theme-items", movies, tvShows);

        return Ok(ComponentResponse.From(response));
    }
}
