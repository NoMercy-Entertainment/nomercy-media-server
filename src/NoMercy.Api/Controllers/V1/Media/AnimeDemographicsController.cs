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
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page", "version"])]
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

        List<GroupCardData> demographicCards =
        [
            .. demographicDtos
                .Where(d => d.TvShowsWithVideo > 0 || d.MoviesWithVideo > 0)
                .Select(dto => new GroupCardData(dto)),
        ];

        if (request.Version != "lolomo")
        {
            ComponentEnvelope response = Component
                .Grid()
                .WithId("anime-demographics")
                .WithItems(demographicCards.Select(card => Component.GroupCard().WithData(card)));

            return Ok(ComponentResponse.From(response));
        }

        List<ComponentEnvelope> components = new();

        foreach (string letter in Letters)
        {
            int index = Array.IndexOf(Letters, letter);

            List<GroupCardData> carouselItems = demographicCards
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
