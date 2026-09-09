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
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Collections;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags(tags: "Media Collections")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/collection/{id:int}")] // match themoviedb.org API
public class CollectionsController(
    ICollectionRepository collectionRepository,
    ILibraryRepository libraryRepository,
    IJobDispatcher jobDispatcher,
    ICollectionMetadataProvider collectionMetadataProvider,
    ILogger<CollectionsController> logger
) : BaseController
{
    [HttpGet]
    [Route("/api/v{version:apiVersion}/collection")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = ["take", "page", "version"])]
    public async Task<IActionResult> Collections(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view collections");

        string language = Language();
        string country = Country();

        // Use optimized query that projects only needed data
        List<CollectionListDto> collectionDtos = await collectionRepository.GetCollectionsListAsync(
            userId,
            language,
            country,
            request.Take,
            request.Page
        );

        if (request.Version != "lolomo")
        {
            List<CardData> cardItems = collectionDtos.Select(dto => new CardData(dto)).ToList();

            ComponentEnvelope response = Component
                .Grid()
                .WithItems(cardItems.Select(item => Component.Card().WithData(item)));

            return Ok(ComponentResponse.From(response));
        }

        List<ComponentEnvelope> carousels = Letters
            .Select(
                (letter, index) =>
                {
                    List<CardData> letterItems = collectionDtos
                        .Select(dto => new CardData(dto))
                        .Where(card => AlphaBucket.Matches(card.TitleSort, letter))
                        .OrderBy(item => item.TitleSort)
                        .ToList();

                    return Component
                        .Carousel()
                        .WithId(letter)
                        .WithTitle(letter)
                        .WithNavigation(
                            index == 0 ? null : Letters[index - 1],
                            index == Letters.Length - 1 ? null : Letters[index + 1]
                        )
                        .WithItems(letterItems.Select(item => Component.Card().WithData(item)))
                        .Build();
                }
            )
            .ToList();

        ComponentEnvelope containerResponse = Component.Container().WithItems(carousels);

        return Ok(containerResponse);
    }

    [HttpGet]
    [ResponseCache(Duration = 300)]
    public async Task<IActionResult> Collection(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view collections");

        string language = Language();
        string country = Country();

        Collection? collection = await collectionRepository.GetCollectionAsync(
            userId,
            id,
            language,
            country, ct);

        if (
            collection is not null
            && collection.CollectionMovies.Count > 0
            && collection.Images.Count > 0
        )
            return Ok(new CollectionResponseDto { Data = new(collection) });

        TmdbCollectionAppends? collectionAppends =
            await collectionMetadataProvider.GetCollectionAsync(id, language, ct);

        if (collectionAppends is null)
            return NotFoundResponse("Collection not found");

        return Ok(new CollectionResponseDto { Data = new(collectionAppends) });
    }

    [HttpGet]
    [Route("available")]
    public async Task<IActionResult> Available(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view collections");

        Collection? collection = await collectionRepository.GetAvailableCollectionAsync(userId, id);

        bool available =
            collection is not null
            && collection.CollectionMovies.Select(movie => movie.Movie.VideoFiles).Any();

        if (!available)
            return NotFoundResponse("Collection not found");

        return Ok(
            new StatusResponseDto<AvailableResponseDto>
            {
                Data = new() { Available = true },
                Status = "ok",
                Message = "Collection is available",
            }
        );
    }

    [HttpGet]
    [Route("watch")]
    public async Task<IActionResult> Watch(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view collections");

        string language = Language();
        string country = Country();

        Collection? collection = await collectionRepository.GetCollectionPlaylistAsync(
            userId,
            id,
            language,
            country
        );

        if (collection is null)
            return NotFoundResponse("Collection not found");

        return Ok(
            collection.CollectionMovies.Select(
                (movie, index) =>
                    new VideoPlaylistResponseDto(
                        movie.Movie,
                        "collection",
                        id,
                        country,
                        index + 1,
                        collection
                    )
            )
        );
    }

    [HttpPost]
    [Route("like")]
    public async Task<IActionResult> Like(
        int id,
        [FromBody] LikeRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to like collections");

        bool success = await collectionRepository.LikeAsync(id, userId, request.Value, ct);

        if (!success)
            return UnprocessableEntityResponse("Collection not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{1}",
                Args = new object[] { request.Value ? "liked" : "unliked" },
            }
        );
    }

    [HttpPost]
    [Route("watch-list")]
    public async Task<IActionResult> AddToWatchList(
        int id,
        [FromBody] WatchListRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to manage watch list");

        bool success = await collectionRepository.AddToWatchListAsync(id, userId, request.Add);

        if (!success)
            return UnprocessableEntityResponse("Collection not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = request.Add
                    ? "Collection added to watch list"
                    : "Collection removed from watch list",
            }
        );
    }

    [HttpDelete]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> DeleteMovie(int id, CancellationToken ct = default)
    {
        await collectionRepository.DeleteAsync(id, ct);

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Movie deleted" });
    }

    [HttpPost]
    [Route("rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct = default)
    {
        Collection? collection = await collectionRepository.GetCollectionForRescanAsync(id, ct);

        if (collection is null)
            return UnprocessableEntityResponse("Collection not found");

        try
        {
            foreach (CollectionMovie collectionMovie in collection.CollectionMovies)
            {
                jobDispatcher.DispatchJob<FileRescanJob>(
                    collectionMovie.MovieId,
                    collectionMovie.Movie.LibraryId
                );
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Rescanning {0} for files in the background",
                Args = [collection.Title],
            }
        );
    }

    [HttpPost]
    [Route("refresh")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Refresh(int id, CancellationToken ct = default)
    {
        Collection? collection = await collectionRepository.GetCollectionWithMovieLibrariesAsync(
            id,
            ct
        );

        if (collection is null)
            return UnprocessableEntityResponse("Collection not found");

        try
        {
            foreach (CollectionMovie collectionMovie in collection.CollectionMovies)
            {
                jobDispatcher.DispatchJob<MovieImportJob>(
                    collectionMovie.MovieId,
                    collectionMovie.Movie.LibraryId
                );
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Refreshing {0} in the background",
                Args = [collection.Title],
            }
        );
    }

    [HttpPost]
    [Route("add")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Add(int id, CancellationToken ct = default)
    {
        Library? library = await libraryRepository.GetLibraryByTypeAsync(
            MediaTypes.MovieMediaType,
            ct: ct
        );

        if (library is null)
            return UnprocessableEntityResponse("No movie library found");

        Collection? collection = await collectionRepository.GetCollectionWithMovieLibrariesAsync(
            id,
            ct
        );

        if (collection is null)
            return UnprocessableEntityResponse("Collection not found");

        try
        {
            foreach (CollectionMovie collectionMovie in collection.CollectionMovies)
            {
                jobDispatcher.DispatchJob<MovieImportJob>(
                    collectionMovie.MovieId,
                    collectionMovie.Movie.LibraryId
                );
            }
        }
        catch (Exception e)
        {
            logger.LogError(e.Message);
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Adding {0} in the background",
                Args = [library.Title],
            }
        );
    }
}
