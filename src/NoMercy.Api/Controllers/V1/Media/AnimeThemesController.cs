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
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
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

        List<GenreCardData> themeCards =
        [
            .. themeDtos
                .Where(t => t.TvShowsWithVideo > 0 || t.MoviesWithVideo > 0)
                .Select(dto => new GenreCardData(dto)),
        ];

        ComponentEnvelope response = Component
            .Grid()
            .WithId("anime-themes")
            .WithItems(themeCards.Select(card => Component.GenreCard().WithData(card)));

        return Ok(ComponentResponse.From(response));
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

        ComponentEnvelope response = TitleCardGrid("anime-theme-items", movies, tvShows, country);

        return Ok(ComponentResponse.From(response));
    }
}
