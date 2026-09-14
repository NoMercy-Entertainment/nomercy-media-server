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
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.NmSystem.Extensions;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Providers.TMDB.Models.TV;
using IJobDispatcher = NoMercy.MediaProcessing.Jobs.IJobDispatcher;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags(tags: "Media TV Shows")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/tv/{id:int}")] // match themoviedb.org API
public class TvShowsController(
    ITvShowRepository tvShowRepository,
    ILibraryRepository libraryRepository,
    IJobDispatcher jobDispatcher,
    ITvShowMetadataProvider tvShowMetadataProvider,
    IEventBus eventBus,
    IMediaTypeClassifier mediaTypeClassifier,
    ILogger<TvShowsController> logger
) : BaseController
{
    [HttpGet]
    [ResponseCache(Duration = 120)]
    public async Task<IActionResult> Tv(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view tv shows");

        string language = Language();
        string country = Country();

        TvDetail? tvDetail = await tvShowRepository.GetTvAsync(userId, id, language, country, ct);

        if (tvDetail is not null)
            return Ok(
                new InfoResponseDto
                {
                    Data = new(tvDetail.Tv, country, tvDetail.Similars, tvDetail.Recommendations),
                }
            );

        TmdbTvShowAppends? tvShowAppends = await tvShowMetadataProvider.GetTvShowAsync(
            id,
            language,
            ct
        );

        if (tvShowAppends is null)
            return NotFoundResponse("Tv show not found");

        return Ok(new InfoResponseDto { Data = new(tvShowAppends, country) });
    }

    [HttpDelete]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> DeleteTv(int id, CancellationToken ct = default)
    {
        await tvShowRepository.DeleteAsync(id, ct);

        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["tv", id.ToString()] });
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["libraries"] });
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["home"] });
        await eventBus.PublishAsync(new LibraryRefreshedEvent { QueryKey = ["continue-watching"] });

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Show deleted" });
    }

    [HttpGet]
    [Route("available")]
    public async Task<IActionResult> Available(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view tv shows");

        bool available = await tvShowRepository.GetTvAvailableAsync(userId, id, ct);

        if (!available)
            return NotFoundResponse("Tv show not found");

        return Ok(
            new StatusResponseDto<AvailableResponseDto>
            {
                Data = new() { Available = true },
                Status = "ok",
                Message = "Tv show is available",
            }
        );
    }

    [HttpGet]
    [Route("watch")]
    public async Task<IActionResult> Watch(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view tv shows");

        string language = Language();
        string country = Country();

        Tv? tv = await tvShowRepository.GetPlaylistAsync(userId, id, language, country, ct);

        if (tv is null)
            return NotFoundResponse("Tv show not found");

        VideoPlaylistResponseDto[] episodes =
        [
            .. tv
                .Seasons.Where(season => season.SeasonNumber > 0)
                .SelectMany(season => season.Episodes)
                .Select(episode => new VideoPlaylistResponseDto(episode, "tv", id, country)),
        ];

        VideoPlaylistResponseDto[] extras =
        [
            .. tv
                .Seasons.Where(season => season.SeasonNumber == 0)
                .SelectMany(season => season.Episodes)
                .Select(episode => new VideoPlaylistResponseDto(episode, "tv", id, country)),
        ];

        VideoPlaylistResponseDto[] result =
        [
            .. episodes.Concat(extras).Where(episode => episode.Id != 0),
        ];

        return Ok(result);
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
            return UnauthorizedResponse("You do not have permission to like tv shows");

        bool success = await tvShowRepository.LikeAsync(id, userId, request.Value, ct);

        if (!success)
            return UnprocessableEntityResponse("Tv show not found");

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

        bool success = await tvShowRepository.AddToWatchListAsync(id, userId, request.Add, ct);

        if (!success)
            return UnprocessableEntityResponse("Tv show not found");

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = request.Add
                    ? "Tv show added to watch list"
                    : "Tv show removed from watch list",
            }
        );
    }

    [HttpPost]
    [Route("rescan")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Rescan(int id, CancellationToken ct = default)
    {
        Tv? tv = await tvShowRepository.GetTvWithLibraryAsync(id, ct);

        if (tv is null)
            return UnprocessableEntityResponse("Tv show not found");

        try
        {
            jobDispatcher.DispatchJob<FileRescanJob>(id, tv.LibraryId);
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
                Args = [tv.Title],
            }
        );
    }

    [HttpPost]
    [Route("refresh")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Refresh(
        int id,
        [FromQuery] Ulid? libraryId = null,
        CancellationToken ct = default
    )
    {
        Tv? tv = await tvShowRepository.GetTvWithLibraryAsync(id, ct);

        if (tv is null)
            return UnprocessableEntityResponse("Tv show not found");

        Ulid targetLibraryId;

        if (libraryId is not null)
        {
            Library? specified = await libraryRepository.GetLibraryByIdLiteAsync(
                libraryId.Value,
                ct
            );

            if (specified is null)
                return NotFoundResponse("Library not found");

            targetLibraryId = specified.Id;
        }
        else
        {
            TmdbTvShowDetails? show = await tvShowMetadataProvider.GetTvShowDetailsAsync(id, ct);
            if (show == null)
                return NotFoundResponse("Tv show not found");

            string? mediaType = await mediaTypeClassifier.ClassifyAsync(
                show.Name,
                show.FirstAirDate.ParseYear(),
                show.OriginCountry
            );

            // null means the classification lookup was inconclusive, not "confirmed
            // not anime" — skip reclassification and keep the show where it already
            // is rather than bounce it on a lookup failure.
            Library? tvLibrary = mediaType is not null
                ? await libraryRepository.GetLibraryByTypeAsync(mediaType, "tv", ct)
                : null;

            targetLibraryId = tvLibrary?.Id ?? tv.Library.Id;
        }

        // The owner pressed add, so the link records that. A show with nothing
        // on disk yet is still a show they asked for, and the listing shows it.
        jobDispatcher.DispatchJob(
            new ShowImportJob
            {
                Id = id,
                LibraryId = targetLibraryId,
                AddedBy = LibraryLinkOrigin.Manual,
            }
        );

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Refreshing {0} data in background",
                Args = [tv.Title],
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
            TmdbTvShowDetails? show = await tvShowMetadataProvider.GetTvShowDetailsAsync(id, ct);
            if (show == null)
                return NotFoundResponse("Tv show not found");

            string? mediaType = await mediaTypeClassifier.ClassifyAsync(
                show.Name,
                show.FirstAirDate.ParseYear(),
                show.OriginCountry
            );

            // No existing placement to fall back to for a brand-new show, so an
            // inconclusive lookup defaults to "tv" the same way a confirmed "not
            // anime" already does.
            library = await libraryRepository.GetLibraryByTypeAsync(mediaType ?? "tv", "tv", ct);

            if (library is null)
                return UnprocessableEntityResponse("No Tv library found");
        }

        try
        {
            jobDispatcher.DispatchJob(
                new ShowImportJob
                {
                    Id = id,
                    LibraryId = library.Id,
                    AddedBy = LibraryLinkOrigin.Manual,
                }
            );
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

    [HttpGet]
    [Route("missing")]
    public async Task<IActionResult> Missing(int id, CancellationToken ct = default)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view library");
        string language = Language();

        IEnumerable<Episode> episodes = await tvShowRepository.GetMissingLibraryShows(
            userId,
            id,
            language,
            ct
        );

        List<IGrouping<long, MissingEpisodeDto>> concat =
        [
            .. episodes
                .Select(episode => new MissingEpisodeDto(episode))
                .OrderBy(episode => episode.SeasonNumber)
                .ThenBy(episode => episode.EpisodeNumber)
                .GroupBy(episode => episode.SeasonNumber),
        ];

        if (concat.Count == 0)
        {
            SeasonCardData noItems = new()
            {
                Id = 0,
                Title = "No missing episodes",
                SeasonNumber = 0,
                EpisodeNumber = 0,
                Overview = "There are no missing episodes in this season.",
                Available = false,
            };

            return Ok(
                ComponentResponse.From(
                    Component
                        .Grid()
                        .WithId("missing-episodes-empty")
                        .WithItems(Component.SeasonCard(noItems).WithWatch().Build())
                )
            );
        }

        return Ok(
            ComponentResponse.From(
                Component
                    .List()
                    .WithId("missing-episodes")
                    .WithItems(
                        concat.SelectMany(seasonGroup =>
                            new ComponentEnvelope[]
                            {
                                // Season title component
                                Component
                                    .SeasonTitle(new((int)seasonGroup.Key, seasonGroup.Count()))
                                    .WithId($"season-{seasonGroup.Key}-title"),
                                // Episodes grid for this season
                                Component
                                    .Grid()
                                    .WithId($"season-{seasonGroup.Key}-episodes")
                                    .WithProperties(new() { { "paddingTop", 16 } })
                                    .WithItems(
                                        seasonGroup.Select(episode =>
                                            Component.SeasonCard(new(episode)).WithWatch()
                                        )
                                    ),
                            }
                        )
                    )
            )
        );
    }
}
