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
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.DTOs.Specials;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media Specials")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/specials")]
public class SpecialController(
    ISpecialRepository specialRepository,
    IJobDispatcher jobDispatcher,
    ILogger<SpecialController> logger
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        List<SpecialCardDto> specials = await specialRepository.GetSpecialCardsAsync(
            userId,
            language,
            request.Take,
            request.Page,
            ct
        );

        if (request.Version != "lolomo")
        {
            List<CardData> cardItems =
            [
                .. specials.Select(special => new CardData(special, country)),
            ];

            ComponentEnvelope response = Component
                .Grid()
                .WithItems(cardItems.Select(item => Component.Card().WithData(item)));

            return Ok(ComponentResponse.From(response));
        }

        List<ComponentEnvelope> carousels =
        [
            .. Letters.Select(letter =>
            {
                List<CardData> letterItems =
                [
                    .. specials
                        .Select(special => new CardData(special, country))
                        .Where(item => AlphaBucket.Matches(item.TitleSort, letter)),
                ];

                return Component
                    .Carousel()
                    .WithId(letter)
                    .WithTitle(letter)
                    .WithItems(letterItems.Select(item => Component.Card().WithData(item)))
                    .Build();
            }),
        ];

        ComponentEnvelope containerResponse = Component.Container().WithItems(carousels);

        return Ok(containerResponse);
    }

    [HttpGet]
    [Route("{id:ulid}")]
    public async Task<IActionResult> Show(Ulid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        string country = Country();

        SpecialDetailDto? detail = await specialRepository.GetSpecialDetailAsync(userId, id, ct);

        if (detail is null)
            return NotFoundResponse("Special not found");

        IEnumerable<int> movieIds = detail
            .Items.Where(item => item.MovieId is not null)
            .Select(item => item.MovieId ?? 0);

        IEnumerable<int> tvIds = detail
            .Items.Where(item => item.EpisodeId is not null)
            .Select(item => item.TvId)
            .Distinct();

        SpecialItemProjections projections = await specialRepository.GetSpecialItemProjectionsAsync(
            userId,
            movieIds,
            tvIds,
            country,
            ct
        );

        List<SpecialItemsDto> items =
        [
            .. projections.Movies.Select(projection => new SpecialItemsDto(projection)),
            .. projections.Tvs.Select(projection => new SpecialItemsDto(projection)),
        ];

        return Ok(new DataResponseDto<SpecialResponseItemDto> { Data = new(detail, items) });
    }

    [HttpGet]
    [Route("{id:ulid}/available")]
    public async Task<IActionResult> Available(Ulid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        Special? special = await specialRepository.GetSpecialAvailableAsync(userId, id);

        bool hasFiles =
            special is not null
            && special.Items.Any(item =>
                (item.Movie?.VideoFiles ?? item.Episode?.VideoFiles ?? []).Any(file =>
                    file.Folder != null
                )
            );

        if (!hasFiles)
            return NotFoundResponse("Special not found");

        return Ok(
            new StatusResponseDto<AvailableResponseDto>
            {
                Data = new() { Available = true },
                Status = "ok",
                Message = "Special is available",
            }
        );
    }

    [HttpGet]
    [Route("{id:ulid}/watch")]
    public async Task<IActionResult> Watch(Ulid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        string language = Language();
        string country = Country();

        Special? special = await specialRepository.GetSpecialPlaylistAsync(
            userId,
            id,
            language,
            country,
            ct
        );

        if (special is null)
            return NotFoundResponse("Special not found");

        VideoPlaylistResponseDto[] items =
        [
            .. special
                .Items.OrderBy(item => item.Order)
                .Select(
                    (item, index) =>
                        item.EpisodeId is not null
                            ? new(item.Episode ?? new Episode(), "specials", id, country, index)
                            : new VideoPlaylistResponseDto(
                                item.Movie ?? new Movie(),
                                "specials",
                                id,
                                country,
                                index
                            )
                ),
        ];

        if (items.Length == 0)
            return NotFoundResponse("Special not found");

        return Ok(items);
    }

    [HttpPost]
    [Route("{id:ulid}/like")]
    public async Task<IActionResult> Like(
        Ulid id,
        [FromBody] LikeRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        Special? special = await specialRepository.LikeSpecialAsync(id, userId, request.Value, ct);

        if (special is null)
            return NotFoundResponse("Special not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0} {1}",
                Args = new object[]
                {
                    special.Title.OrEmpty(),
                    request.Value ? "liked" : "unliked",
                },
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/watch-list")]
    public async Task<IActionResult> AddToWatchList(
        Ulid id,
        [FromBody] WatchListRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();

        bool success = await specialRepository.AddToWatchListAsync(id, userId, request.Add, ct);

        if (!success)
            return UnprocessableEntityResponse("Special not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = request.Add
                    ? "Special added to watch list"
                    : "Special removed from watch list",
            }
        );
    }

    [HttpPost]
    [Route("{id:ulid}/rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(Ulid id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();

        Special? special = await specialRepository.GetSpecialWithTvAsync(userId, id, ct);

        if (special is null)
            return UnprocessableEntityResponse("Special not found");

        IEnumerable<(int Id, Ulid LibraryId)> movies = special
            .Items.Where(item => item.MovieId is not null)
            .Select(item => (item.MovieId ?? 0, item.Movie!.LibraryId));
        IEnumerable<(int Id, Ulid LibraryId)> shows = special
            .Items.Where(item => item.Episode is not null)
            .Select(item => (item.Episode!.TvId, item.Episode.Tv.LibraryId));

        foreach ((int mediaId, Ulid libraryId) in movies.Concat(shows.Distinct()))
        {
            try
            {
                jobDispatcher.DispatchJob<FileRescanJob>(mediaId, libraryId);
            }
            catch (Exception e)
            {
                logger.LogError(e.Message);
                return InternalServerErrorResponse(e.Message);
            }
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Rescanning {0} for files in the background",
                Args = [special.Title ?? "Unknown"],
            }
        );
    }

    // [HttpPost]
    // [Route("{id:ulid}/refresh")]
    // [Authorize(Policy = "Moderator")]
    // public async Task<IActionResult> Refresh(Ulid id, CancellationToken ct = default)
    // {
    //     Special? special = await specialRepository.GetSpecialByIdAsync(id, ct);
    //
    //     if (special is null)
    //         return UnprocessableEntityResponse("Special not found");
    //
    //     try
    //     {
    //         jobDispatcher.DispatchJob<MovieImportJob>(id, special.Library.Id);
    //     }
    //     catch (Exception e)
    //     {
    //         logger.LogError(e.Message);
    //         return InternalServerErrorResponse(e.Message);
    //     }
    //
    //     return Ok(
    //         new StatusResponseDto<string>
    //         {
    //             Status = "ok",
    //             Message = "Refreshing {0} in the background",
    //             Args = [special.Title ?? "Unknown"],
    //         }
    //     );
    // }

    [HttpPost]
    [Route("seed")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Seed(CancellationToken ct = default)
    {
        try
        {
            jobDispatcher.DispatchJob<SpecialSeedFetchJob>();
        }
        catch (Exception e)
        {
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Seeding specials in the background",
            }
        );
    }
}
