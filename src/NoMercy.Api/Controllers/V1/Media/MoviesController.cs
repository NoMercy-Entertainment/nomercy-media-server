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
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.Movies;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags(tags: "Media Movies")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/movie/{id:int}")] // match themoviedb.org API
public class MoviesController(
    IMovieRepository movieRepository,
    ILibraryRepository libraryRepository,
    IJobDispatcher jobDispatcher,
    IMovieMetadataProvider movieMetadataProvider,
    IServerConfiguration config,
    IEventBus eventBus,
    ILogger<MoviesController> logger
) : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 120)]
    public async Task<IActionResult> Movie(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view movies");

        string language = Language();
        string country = Country();

        Movie? movie = await movieRepository.GetMovieDetailAsync(userId, id, language, country, ct);

        if (movie is not null)
            return Ok(new InfoResponseDto { Data = new(movie, country) });

        try
        {
            TmdbMovieAppends? movieAppends = await movieMetadataProvider.GetMovieAsync(
                id,
                language,
                ct
            );

            if (movieAppends is null)
                return NotFoundResponse("Movie not found");

            if (movieAppends.Adult && !config.ShowAdultContent)
                return UnauthorizedResponse(
                    "Movie is adult which is not allowed by the server configuration"
                );

            return Ok(new InfoResponseDto { Data = new(movieAppends, country) });
        }
        catch (Exception)
        {
            return NotFoundResponse("Movie not found");
        }
    }

    [HttpDelete]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> DeleteMovie(int id, CancellationToken ct = default)
    {
        await movieRepository.DeleteAsync(id, ct);

        await eventBus.PublishAsync(
            new LibraryRefreshedEvent { QueryKey = ["movie", id.ToString()] }
        );
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["libraries"] });
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["home"] });
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue-watching"] });

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Movie deleted" });
    }

    [HttpGet]
    [Route("available")]
    public async Task<IActionResult> Available(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view movies");

        bool available = await movieRepository.GetMovieAvailableAsync(userId, id, ct);

        if (!available)
            return NotFoundResponse("Movie not found");

        return Ok(
            new StatusResponseDto<AvailableResponseDto>
            {
                Data = new() { Available = true },
                Status = "ok",
                Message = "Movie is available",
            }
        );
    }

    [HttpGet]
    [Route("watch")]
    public async Task<IActionResult> Watch(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view movies");

        string language = Language();
        string country = Country();

        IEnumerable<VideoPlaylistResponseDto> playlist = (
            await movieRepository.GetMoviePlaylistAsync(userId, id, language, country, ct)
        ).Select(movie => new VideoPlaylistResponseDto(
            movie,
            MediaTypes.MovieMediaType,
            id,
            country
        ));

        if (!playlist.Any())
            return NotFoundResponse("Movie not found");

        return Ok(playlist);
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
            return UnauthorizedResponse("You do not have permission to like movies");

        bool success = await movieRepository.LikeMovieAsync(id, userId, request.Value, ct);

        if (!success)
            return UnprocessableEntityResponse("Movie not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "{0}: {1}",
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

        bool success = await movieRepository.AddToWatchListAsync(id, userId, request.Add, ct);

        if (!success)
            return UnprocessableEntityResponse("Movie not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = request.Add
                    ? "Movie added to watch list"
                    : "Movie removed from watch list",
            }
        );
    }

    [HttpPost]
    [Route("rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct = default)
    {
        Movie? movie = await movieRepository.GetMovieForRescanAsync(id, ct);

        if (movie is null)
            return UnprocessableEntityResponse("Movie not found");

        try
        {
            jobDispatcher.DispatchJob<FileRescanJob>(id, movie.LibraryId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Message}", e.Message);
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Rescanning {0} for files in the background",
                Args = [movie.Title],
            }
        );
    }

    [HttpPost]
    [Route("refresh")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Refresh(int id, CancellationToken ct = default)
    {
        Movie? movie = await movieRepository.GetMovieForRefreshAsync(id, ct);

        if (movie is null)
            return UnprocessableEntityResponse("Movie not found");

        try
        {
            jobDispatcher.DispatchJob<MovieImportJob>(id, movie.Library.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Message}", e.Message);
            return InternalServerErrorResponse(e.Message);
        }

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Refreshing {0} in the background",
                Args = [movie.Title],
            }
        );
    }

    [HttpPost]
    [Route("add")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Add(
        int id,
        [FromQuery] Ulid? libraryId = null,
        CancellationToken ct = default
    )
    {
        Library? library;

        if (libraryId is not null)
        {
            library = await libraryRepository.GetLibraryByIdLiteAsync(libraryId.Value, ct);

            if (library is null)
                return NotFoundResponse("Library not found");
        }
        else
        {
            library = await libraryRepository.GetLibraryByTypeAsync(
                MediaTypes.MovieMediaType,
                null,
                ct
            );

            if (library is null)
                return UnprocessableEntityResponse("No movie library found");
        }

        try
        {
            jobDispatcher.DispatchJob<MovieImportJob>(id, library.Id);
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Message}", e.Message);
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
