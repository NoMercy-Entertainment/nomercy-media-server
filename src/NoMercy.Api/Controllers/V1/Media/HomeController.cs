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

using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.Services;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.Trailers;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}")]
public partial class HomeController : BaseController
{
    // YouTube video ids are exactly 11 chars of [A-Za-z0-9_-]. trailerId flows
    // into shell command strings (yt-dlp/ffmpeg) and filesystem paths, so a
    // strict match is the trust boundary that blocks command injection and
    // path traversal before the value reaches Shell.Exec* or Path.Combine.
    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex TrailerIdRegex();

    private readonly HomeService _homeService;
    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly IStorage _transcodeStorage;

    private readonly ILogger<HomeController> _logger;

    public HomeController(
        ILogger<HomeController> logger,
        HomeService homeService,
        IDbContextFactory<MediaContext> contextFactory,
        [FromKeyedServices("transcode")] IStorage transcodeStorage
    )
    {
        _logger = logger;
        _homeService = homeService;
        _contextFactory = contextFactory;
        _transcodeStorage = transcodeStorage;
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

        LibraryRepository libraryRepository = new(_contextFactory);
        List<Library> libraries = await libraryRepository.GetLibrariesLite(userId, ct);

        // Fetch all library data in parallel - each task needs its own MediaContext for thread safety
        Task<(Library library, List<Movie> movies, List<Tv> shows)>[] libraryDataTasks =
        [
            .. libraries.Select(async library =>
            {
                await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
                List<Movie> libraryMovies = [];
                await foreach (
                    Movie movie in libraryRepository
                        .GetLibraryMovies(
                            context,
                            userId,
                            library.Id,
                            language,
                            UiLimits.MaximumCardsInCarousel,
                            request.Page,
                            m => m.CreatedAt,
                            "desc"
                        )
                        .WithCancellation(ct)
                )
                {
                    libraryMovies.Add(movie);
                }

                List<Tv> libraryShows = [];
                await foreach (
                    Tv tv in libraryRepository
                        .GetLibraryShows(
                            context,
                            userId,
                            library.Id,
                            language,
                            UiLimits.MaximumCardsInCarousel,
                            request.Page,
                            m => m.CreatedAt,
                            "desc"
                        )
                        .WithCancellation(ct)
                )
                {
                    libraryShows.Add(tv);
                }

                return (library, libraryMovies, libraryShows);
            }),
        ];

        (Library library, List<Movie> movies, List<Tv> shows)[] libraryDataResults =
            await Task.WhenAll(libraryDataTasks);

        foreach (
            (
                Library library,
                List<Movie> libraryMovies,
                List<Tv> libraryShows
            ) in libraryDataResults.OrderByDescending(r => r.library.Order)
        )
        {
            response.Data = response.Data.Prepend(
                new()
                {
                    Title = "Latest in " + library.Title,
                    MoreLink = new($"/libraries/{library.Id}", UriKind.Relative),
                    Items = libraryMovies
                        .Select(movie => new GenreRowItemDto(movie, country))
                        .Concat(libraryShows.Select(tv => new GenreRowItemDto(tv, country))),
                }
            );
        }

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
        if (!TrailerIdRegex().IsMatch(trailerId))
            return NotFoundResponse("Trailer not found");

        string infoJsonPath = _transcodeStorage.CombinePath(trailerId, "info.json");

        if (await _transcodeStorage.ExistsAsync(infoJsonPath, ct))
        {
            string text = await _transcodeStorage.ReadAllTextAsync(infoJsonPath, ct);
            TrailerInfo? trailerInfo = text.FromJson<TrailerInfo>();
            if (trailerInfo is not null)
            {
                return Ok(
                    new StatusResponseDto<string> { Status = "ok", Message = "Trailer found" }
                );
            }
        }

        string arg =
            $"-f bestvideo+bestaudio -j https://youtube.com/watch?v={trailerId} --extractor-args \"youtube:player_client=default\" ";
        Shell.ExecResult result = await Shell.ExecAsync(AppFiles.YtdlpPath, arg);

        if (!result.Success || string.IsNullOrEmpty(result.StandardOutput))
        {
            _logger.LogError(result.StandardError);
            return NotFoundResponse("Trailer not found");
        }

        if (!await _transcodeStorage.ExistsAsync(trailerId, ct))
            await _transcodeStorage.CreateDirectoryAsync(trailerId, ct);

        await _transcodeStorage.WriteAllTextAsync(infoJsonPath, result.StandardOutput, ct);

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Trailer found" });
    }

    [HttpGet]
    [Route("trailer/{trailerId}")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> Trailer(string trailerId, CancellationToken ct = default)
    {
        if (!TrailerIdRegex().IsMatch(trailerId))
            return NotFoundResponse("Trailer not found");

        string language = Language();

        if (!await _transcodeStorage.ExistsAsync(trailerId, ct))
            await _transcodeStorage.CreateDirectoryAsync(trailerId, ct);

        string infoJsonPath = _transcodeStorage.CombinePath(trailerId, "info.json");
        string text = await _transcodeStorage.ReadAllTextAsync(infoJsonPath, ct);
        TrailerInfo? trailerInfo = text.FromJson<TrailerInfo>();

        if (trailerInfo is null)
        {
            _logger.LogError("Trailer info is null");
            return NotFoundResponse("Trailer not found");
        }

        string firstSegmentPath = _transcodeStorage.CombinePath(trailerId, "video_00002.ts");
        if (await _transcodeStorage.ExistsAsync(firstSegmentPath, ct))
        {
            return Ok(TrailerPlaylist(trailerInfo, trailerId));
        }

        string trailerWorkDir = Path.Combine(AppFiles.TranscodePath, trailerId);

        _ = Task.Run(
            () =>
            {
                try
                {
                    string command = TrailerCommandBuilder.Build(
                        AppFiles.YtdlpPath,
                        AppFiles.FfmpegPath,
                        trailerId,
                        language
                    );

                    if (Software.IsWindows)
                    {
                        _logger.LogDebug("cmd -c \"{Command}\"", command);
                        Shell.ExecSync(
                            "cmd",
                            $"/c \"{command}\"",
                            new() { WorkingDirectory = trailerWorkDir }
                        );
                    }
                    else
                    {
                        _logger.LogDebug("/bin/bash -c \"{Command}\"", command);
                        Shell.ExecSync(
                            "/bin/bash",
                            $"-c \"{command}\"",
                            new() { WorkingDirectory = trailerWorkDir }
                        );
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "Trailer download failed for {TrailerId}: {Message}",
                        trailerId,
                        ex.Message
                    );
                }
            },
            ct
        );

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            HttpContext.RequestAborted
        );
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await _transcodeStorage.ExistsAsync(firstSegmentPath, ct))
        {
            await Task.Delay(1000, timeoutCts.Token);
        }

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
        if (!TrailerIdRegex().IsMatch(trailerId))
            return NotFoundResponse("Trailer not found");

        if (!await _transcodeStorage.ExistsAsync(trailerId, ct))
            return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Trailer removed" });

        string trailerAbsPath = Path.Combine(AppFiles.TranscodePath, trailerId);

        try
        {
            await _transcodeStorage.DeleteDirectoryAsync(trailerId, recursive: true, ct: ct);
            _logger.LogInformation("Trailer folder deleted: {TrailerAbsPath}", trailerAbsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "Failed to delete trailer folder {TrailerAbsPath}: {Message}",
                trailerAbsPath,
                ex.Message
            );
            return InternalServerErrorResponse("Failed to remove trailer");
        }

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Trailer removed" });
    }
}
