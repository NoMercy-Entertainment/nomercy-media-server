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
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.MediaProcessing.Trailers;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}")]
public class HomeController : BaseController
{
    private readonly HomeService _homeService;
    private readonly ITrailerCache _trailerCache;

    private readonly ILogger<HomeController> _logger;

    public HomeController(
        ILogger<HomeController> logger,
        HomeService homeService,
        ITrailerCache trailerCache
    )
    {
        _logger = logger;
        _homeService = homeService;
        _trailerCache = trailerCache;
    }

    [HttpGet]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Index(
        [FromQuery] PageRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        string language = Language();
        string country = Country();

        List<GenreRowDto<GenreRowItemDto>> result = await _homeService.GetHomePageContent(
            userId,
            language,
            country,
            request
        );

        List<GenreRowDto<GenreRowItemDto>> newData = [.. result];
        bool hasMore = newData.Count >= request.Take;

        newData = [.. newData.Take(request.Take)];

        PaginatedResponse<GenreRowDto<GenreRowItemDto>> response = new()
        {
            Data = newData,
            NextPage = hasMore ? request.Page + 1 : null,
            HasMore = hasMore,
        };

        if (request.Page != 0)
            return Ok(response);

        // "Latest in {library}" carousels belong to the non-lolomo home only;
        // the lolomo (mobile/TV) variant lays those library rows out itself.
        if (request.Version == "lolomo")
            return Ok(response);

        foreach (
            GenreRowDto<GenreRowItemDto> row in await _homeService.GetLatestInLibraryRowsAsync(
                userId,
                language,
                country,
                ct
            )
        )
            response.Data = response.Data.Prepend(row);

        return Ok(response);
    }

    [HttpGet("home")]
    [ResponseCache(NoStore = true)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Home([FromQuery] PageRequestDto request)
    {
        ComponentResponse result = await _homeService.GetHomeData(
            User.UserId(),
            Language(),
            Country(),
            request.Version
        );

        return Ok(result);
    }

    [HttpPost("home/card")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> HomeCard([FromBody] CardRequestDto request)
    {
        ComponentResponse result = await _homeService.GetHomeCard(
            User.UserId(),
            Language(),
            Country(),
            request.ReplaceId
        );

        return Ok(result);
    }

    [HttpGet("home/tv")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> HomeTv()
    {
        ComponentResponse result = await _homeService.GetHomeTvContent(
            User.UserId(),
            Language(),
            Country()
        );

        return Ok(result);
    }

    [HttpPost("home/continue")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> HomeContinue([FromBody] CardRequestDto request)
    {
        ComponentResponse result = await _homeService.GetHomeContinueContent(
            User.UserId(),
            Language(),
            Country(),
            request.ReplaceId
        );

        return Ok(result);
    }

    [HttpHead]
    [Route("trailer/{trailerId}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> HasTrailer(string trailerId, CancellationToken ct = default)
    {
        if (
            !TrailerCache.IsValidId(trailerId) || !await _trailerCache.FetchInfoAsync(trailerId, ct)
        )
            return NotFoundResponse("Trailer not found");

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Trailer found" });
    }

    [HttpGet]
    [Route("trailer/{trailerId}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Trailer(string trailerId, CancellationToken ct = default)
    {
        if (!TrailerCache.IsValidId(trailerId))
            return NotFoundResponse("Trailer not found");

        TrailerInfo? trailerInfo = await _trailerCache.ReadInfoAsync(trailerId, ct);
        if (trailerInfo is null)
        {
            _logger.LogError("Trailer info is null");
            return NotFoundResponse("Trailer not found");
        }

        await _trailerCache.EnsureSegmentsAsync(trailerId, Language(), ct);
        return Ok(TrailerPlaylist(trailerInfo, trailerId));
    }

    private static VideoPlaylistResponseDto TrailerPlaylist(
        TrailerInfo trailerInfo,
        string trailerId
    ) =>
        new VideoPlaylistResponseDto
        {
            Id = 0,
            Title = trailerInfo.Title,
            Description = trailerInfo.Description,
            Duration = trailerInfo.Duration.ToHis(),
            Image = trailerInfo.Thumbnail?.ToString(),
            File = $"/transcodes/{trailerId}/video.m3u8",
            Origin = Info.DeviceId,
            PlaylistId = trailerInfo.Id!,
            Tracks =
            [
                .. trailerInfo
                    .Subtitles.Where(t => t.Value.Any(s => s.Ext == "vtt"))
                    .Select(t => new VideoTrack
                    {
                        Label = t.Value.First(s => s.Ext == "vtt").Name,
                        File = $"/transcodes/{trailerId}/-.{t.Key}.vtt",
                        Language = t.Key,
                        Kind = "subtitles",
                    }),
            ],
            Sources =
            [
                new()
                {
                    Src = $"/transcodes/{trailerId}/video.m3u8",
                    Type = "application/x-mpegURL",
                    Languages = [trailerInfo.Language.OrEmpty()],
                },
            ],
        };

    [HttpDelete]
    [Route("trailer/{trailerId}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> RemoveTrailer(string trailerId, CancellationToken ct = default)
    {
        if (!TrailerCache.IsValidId(trailerId))
            return NotFoundResponse("Trailer not found");

        if (!await _trailerCache.RemoveAsync(trailerId, ct))
            return InternalServerErrorResponse("Failed to remove trailer");

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Trailer removed" });
    }
}
