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
[Tags("Anime Demographics")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/anime/demographics")]
public class AnimeDemographicsController(IAnimeDemographicRepository animeDemographicRepository)
    : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
    public async Task<IActionResult> Demographics(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        string language = Language();

        List<AnimeDemographicWithCountsDto> demographicDtos =
            await animeDemographicRepository.GetDemographicsWithCountsAsync(
                userId,
                language,
                request.Take,
                request.Page,
                ct
            );

        List<GenreCardData> demographicCards =
        [
            .. demographicDtos
                .Where(d => d.TvShowsWithVideo > 0 || d.MoviesWithVideo > 0)
                .Select(dto => new GenreCardData(dto)),
        ];

        ComponentEnvelope response = Component
            .Grid()
            .WithId("anime-demographics")
            .WithItems(demographicCards.Select(card => Component.GenreCard().WithData(card)));

        return Ok(ComponentResponse.From(response));
    }

    [HttpGet]
    [Route("{demographicId}")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page"])]
    public async Task<IActionResult> Demographic(
        int demographicId,
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        (
            AnimeDemographicDetailDto? demographicDetail,
            List<HomeMovieCardDto> movies,
            List<HomeTvCardDto> tvShows
        ) = await animeDemographicRepository.GetDemographicCardsAsync(
            userId,
            demographicId,
            language,
            country,
            request.Take,
            request.Page,
            ct
        );

        if (demographicDetail is null || (movies.Count == 0 && tvShows.Count == 0))
            return NotFoundResponse("Anime demographic not found");

        ComponentEnvelope response = TitleCardGrid("anime-demographic-items", movies, tvShows);

        return Ok(ComponentResponse.From(response));
    }
}
