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

using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Media.Subtitles.Dtos;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services.Video;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Subtitles;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Networking;
using NoMercy.Storage;

namespace NoMercy.Api.Controllers.V1.Media.Subtitles;

/// <summary>
/// Playback-time subtitle search + download. Reuses the encoder's OpenSubtitles
/// integration (<see cref="IOpenSubtitlesAdapter"/>) to find and fetch subtitles
/// matching whatever the requesting user is currently watching, then persists the
/// chosen candidate as a permanent VTT sidecar next to the video file.
/// </summary>
[ApiController]
[Tags("Media Subtitles")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/subtitles")]
public class SubtitlesController(
    VideoPlaylistManager videoPlaylistManager,
    IVideoFileRepository videoFileRepository,
    IFileRepository fileRepository,
    IFolderRepository folderRepository,
    IOpenSubtitlesAdapter openSubtitlesAdapter,
    IStorageFactory storageFactory,
    ILogger<SubtitlesController> logger
) : BaseController
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(5);

    // Subtitles downloaded through this endpoint are always plain (non-sign,
    // non-song) full-length tracks — the only variant the search endpoint offers.
    private const string DownloadedSubtitleType = "full";
    private const string SidecarExtension = "vtt";

    /// <summary>
    /// Searches OpenSubtitles for the video file identified by <paramref name="type"/> +
    /// <paramref name="id"/> (optionally narrowed to a specific <paramref name="videoFileId"/>).
    /// Tries a moviehash match first, falls back to filename, then title/season/episode —
    /// stopping at the first strategy that returns results.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string type,
        [FromQuery] int id,
        [FromQuery] string? videoFileId,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to view subtitles");

        if (type != MediaTypes.MovieMediaType && type != MediaTypes.TvMediaType)
            return BadRequestResponse($"Invalid type '{type}'. Expected 'movie' or 'tv'.");

        (IActionResult? rejection, VideoPlaylistResponseDto? target, VideoFile? file) =
            await ResolveVideoFileAsync(userId, type, id, videoFileId, ct);
        if (target is null || file is null)
            return rejection!;

        string language = Language();

        string[] languages = ResolveLanguages(Request.Query, language);

        IStorage? storage = await ResolveStorageAsync(file);
        (string? movieHash, long fileSize) = storage is null
            ? (null, 0)
            : TryComputeHash(storage, file);

        if (storage is null)
            logger.LogWarning(
                "No storage backend for video file {VideoFileId} (share {Share}) — "
                    + "falling back to filename/title search",
                file.Id,
                file.Share
            );

        if (openSubtitlesAdapter.IsRateLimited)
            return TooManyRequestsResponse(
                "OpenSubtitles is rate-limited by the upstream provider. Try again in a few minutes."
            );

        IReadOnlyList<SubtitleCandidate> candidates = [];

        if (movieHash is not null)
            candidates = await openSubtitlesAdapter.SearchByHashAsync(
                movieHash,
                fileSize,
                languages,
                SearchTimeout,
                ct,
                priority: true
            );

        if (candidates.Count == 0)
            candidates = await openSubtitlesAdapter.SearchByFilenameAsync(
                file.Filename,
                languages,
                SearchTimeout,
                ct,
                priority: true
            );

        if (candidates.Count == 0)
        {
            bool isTv = type == MediaTypes.TvMediaType;
            string title = (isTv ? target.Show : target.Title) ?? file.Filename;
            int? season = isTv ? target.Season : null;
            int? episode = isTv ? target.Episode : null;
            int? year = target.Year > 0 ? (int)target.Year : null;

            candidates = await openSubtitlesAdapter.SearchByTitleAsync(
                title,
                season,
                episode,
                year,
                languages,
                SearchTimeout,
                ct,
                priority: true
            );
        }

        // The adapter swallows OpenSubtitlesRateLimitException internally (by design, so an
        // encode never fails on a rate-limited subtitle lookup) and returns an empty list
        // instead. Re-check the flag so a rate-limited search reports 429, not an empty 200.
        if (candidates.Count == 0 && openSubtitlesAdapter.IsRateLimited)
            return TooManyRequestsResponse(
                "OpenSubtitles is rate-limited by the upstream provider. Try again in a few minutes."
            );

        List<SubtitleSearchResultDto> results = candidates.Select(MapToDto).ToList();

        return Ok(
            new StatusResponseDto<List<SubtitleSearchResultDto>>
            {
                Status = "ok",
                Data = results,
                Message = "Found {0} subtitle(s)",
                Args = [results.Count],
            }
        );
    }

    /// <summary>
    /// Downloads the subtitle candidate identified by <see cref="SubtitleDownloadRequestDto.DownloadUrl"/>
    /// (the opaque token returned by <c>GET subtitles/search</c>), converts it to WebVTT, writes it
    /// as a permanent sidecar next to the video file, and registers it on <see cref="VideoFile.Subtitles"/>
    /// so it appears in every future watch response. Returns the sidecar's playable URL so the caller
    /// can also add it to the CURRENT playback session immediately, without waiting on a re-fetch.
    /// </summary>
    [HttpPost("download")]
    public async Task<IActionResult> Download(
        [FromBody] SubtitleDownloadRequestDto request,
        CancellationToken ct = default
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse("You do not have permission to download subtitles");

        IActionResult? invalid = await ValidateDownloadRequestAsync(request, ct);
        if (invalid is not null)
            return invalid;

        (IActionResult? rejection, _, VideoFile? file) = await ResolveVideoFileAsync(
            userId,
            request.Type,
            request.Id,
            request.VideoFileId,
            ct
        );
        if (file is null)
            return rejection!;

        (IActionResult? failed, string? vttContent) = await DownloadAsVttAsync(request, ct);
        if (vttContent is null)
            return failed!;

        // Mirrors VideoPlaylistResponseDto.Subtitles(VideoFile)'s (pre-existing, unowned by this
        // slice) URL construction exactly — that private helper is what the NEXT watch response
        // reads to build the track URL, so the sidecar must land at the exact path it expects:
        // "{baseFolder}/subtitles{filenameWithoutExt}.{language}.{type}.{ext}" with no separator
        // between "subtitles" and the filename. Any change to that helper must update this too.
        string filenameNoExt = file.Filename.OrEmpty().Replace(".mp4", "").Replace(".m3u8", "");
        string sidecarFileName =
            $"subtitles{filenameNoExt}.{request.Language}.{DownloadedSubtitleType}.{SidecarExtension}";
        IActionResult? writeFailed = await WriteSidecarAsync(file, sidecarFileName, vttContent, ct);
        if (writeFailed is not null)
            return writeFailed;

        await RegisterSubtitleAsync(file, request.Language, ct);

        string baseFolder = $"/{file.Share}{file.Folder}".EncodePath();
        string trackUrl =
            $"{baseFolder}/subtitles{filenameNoExt.EncodePath()}.{request.Language}.{DownloadedSubtitleType}.{SidecarExtension}";

        SubtitleDownloadResultDto result = new(
            File: trackUrl,
            Kind: "subtitles",
            Label: DownloadedSubtitleType,
            Language: request.Language
        );

        return Ok(
            new StatusResponseDto<SubtitleDownloadResultDto>
            {
                Status = "ok",
                Data = result,
                Message = "Subtitle downloaded for {0}",
                Args = [file.Filename],
            }
        );
    }

    private async Task<IActionResult?> ValidateDownloadRequestAsync(
        SubtitleDownloadRequestDto request,
        CancellationToken ct
    )
    {
        if (request.Type != MediaTypes.MovieMediaType && request.Type != MediaTypes.TvMediaType)
            return BadRequestResponse($"Invalid type '{request.Type}'. Expected 'movie' or 'tv'.");

        if (string.IsNullOrWhiteSpace(request.DownloadUrl))
            return BadRequestResponse("downloadUrl is required");

        // download_url is the opaque token from subtitles/search, but nothing forces
        // that: it is fetched server-side, so an authenticated user could point it at
        // loopback/LAN/cloud-metadata (SSRF). Reject any URL that is not an http(s)
        // link to a publicly routable host before doing anything else.
        if (!await ServerSideRequestGuard.IsSafePublicHttpUrlAsync(request.DownloadUrl, ct))
            return BadRequestResponse(
                "download_url must be an absolute http(s) URL that resolves to a public host."
            );

        if (string.IsNullOrWhiteSpace(request.Language))
            return BadRequestResponse("language is required");

        return null;
    }

    private async Task<(IActionResult? Failed, string? VttContent)> DownloadAsVttAsync(
        SubtitleDownloadRequestDto request,
        CancellationToken ct
    )
    {
        SubtitleCandidate candidate = new(
            Provider: "OpenSubtitles",
            Language: request.Language,
            Rating: 0,
            Downloads: 0,
            IsTrustedUploader: false,
            Fps: null,
            DownloadUrl: request.DownloadUrl,
            Format: (request.Format ?? "srt").Trim().ToLowerInvariant()
        );

        byte[] rawBytes;
        try
        {
            // Someone is sat in front of the player waiting on this, so it jumps the queue ahead
            // of whatever the backlog sweep has already stacked up.
            rawBytes = await openSubtitlesAdapter.DownloadAsync(candidate, ct, priority: true);
        }
        catch (OpenSubtitlesRateLimitException)
        {
            return (
                TooManyRequestsResponse(
                    "OpenSubtitles is rate-limited by the upstream provider. Try again in a few minutes."
                ),
                null
            );
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(
                ex,
                "Failed to download subtitle from {DownloadUrl}",
                request.DownloadUrl
            );
            return (
                InternalServerErrorResponse($"Failed to download subtitle: {ex.Message}"),
                null
            );
        }

        string vttContent;
        try
        {
            vttContent = ConvertToVtt(Encoding.UTF8.GetString(rawBytes), candidate.Format);
        }
        catch (NotSupportedException)
        {
            logger.LogWarning(
                "Subtitle format {Format} is not convertible to WebVTT",
                candidate.Format
            );
            return (
                UnprocessableEntityResponse(
                    $"Subtitle format '{candidate.Format}' is not supported — only SRT and VTT can be "
                        + "converted to the WebVTT sidecar the player expects."
                ),
                null
            );
        }

        return (null, vttContent);
    }

    private async Task<IActionResult?> WriteSidecarAsync(
        VideoFile file,
        string sidecarFileName,
        string vttContent,
        CancellationToken ct
    )
    {
        IStorage? storage = await ResolveStorageAsync(file);
        if (storage is null)
        {
            logger.LogError(
                "Cannot write subtitle sidecar: no storage backend for video file {VideoFileId} "
                    + "(share {Share})",
                file.Id,
                file.Share
            );
            return InternalServerErrorResponse(
                "Could not resolve the storage this video lives on."
            );
        }

        string sidecarPath = storage.CombinePath(file.HostFolder, sidecarFileName);

        try
        {
            byte[] vttBytes = Encoding.UTF8.GetBytes(vttContent);
            await using Stream writeStream = storage.OpenWrite(sidecarPath, overwrite: true);
            await writeStream.WriteAsync(vttBytes, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write subtitle sidecar to {SidecarPath}", sidecarPath);
            return InternalServerErrorResponse(
                $"Failed to write subtitle sidecar to storage: {ex.Message}"
            );
        }

        return null;
    }

    /// <summary>
    /// Converts a downloaded candidate's raw text to the WebVTT the server serves. SRT is the
    /// overwhelming majority of OpenSubtitles results and converts losslessly; VTT candidates pass
    /// through unchanged. Anything else (ASS/SSA/SUB) isn't renderable by the VTT-only player, so
    /// it's rejected rather than written as a broken sidecar.
    /// </summary>
    private static string ConvertToVtt(string rawText, string format) =>
        format switch
        {
            "srt" or "subrip" => SubtitleFormatConverter.SrtToVtt(rawText),
            "vtt" or "webvtt" => rawText,
            _ => throw new NotSupportedException(format),
        };

    /// <summary>
    /// The video file a subtitle request targets: the requested file when it belongs to the
    /// media, otherwise the media's current item. Returns the HTTP rejection when there is none.
    /// </summary>
    private async Task<(
        IActionResult? Rejection,
        VideoPlaylistResponseDto? Target,
        VideoFile? File
    )> ResolveVideoFileAsync(
        Guid userId,
        string type,
        int id,
        string? videoFileId,
        CancellationToken ct
    )
    {
        Ulid? requestedVideoFileId = null;
        if (!string.IsNullOrWhiteSpace(videoFileId))
        {
            if (!Ulid.TryParse(videoFileId, out Ulid parsedVideoFileId))
                return (BadRequestResponse("Invalid videoFileId"), null, null);
            requestedVideoFileId = parsedVideoFileId;
        }

        (VideoPlaylistResponseDto? Item, List<VideoPlaylistResponseDto> Playlist) resolved;
        try
        {
            // VideoPlaylistManager resolves listId via int.Parse(dynamic), so it must arrive
            // as a string, the shape SignalR hands it, or the dynamic dispatch throws.
            resolved = await videoPlaylistManager.GetPlaylist(
                userId,
                type,
                id.ToString(),
                null,
                Language(),
                Country()
            );
        }
        catch (ArgumentException)
        {
            return (
                BadRequestResponse($"Invalid type '{type}'. Expected 'movie' or 'tv'."),
                null,
                null
            );
        }

        VideoPlaylistResponseDto? target = requestedVideoFileId is not null
            ? resolved.Playlist.FirstOrDefault(p => p.VideoId == requestedVideoFileId.Value)
                ?? resolved.Item
            : resolved.Item;

        if (target is null)
            return (NotFoundResponse("No video found for the given media"), null, null);

        VideoFile? file = await videoFileRepository.GetByIdAsync(target.VideoId, ct);
        if (file is null)
            return (NotFoundResponse("Video file not found"), null, null);

        return (null, target, file);
    }

    /// <summary>
    /// Merges the downloaded subtitle into <see cref="VideoFile.Subtitles"/> — the JSON column
    /// <see cref="VideoPlaylistResponseDto"/> reads to build <c>tracks</c> on every future watch
    /// response — replacing any existing entry for the same language + type so re-downloading a
    /// language doesn't leave duplicate track entries.
    /// </summary>
    private async Task RegisterSubtitleAsync(VideoFile file, string language, CancellationToken ct)
    {
        List<VideoPlaylistResponseDto.Subtitle> subtitles;
        try
        {
            subtitles =
                JsonConvert.DeserializeObject<List<VideoPlaylistResponseDto.Subtitle>>(
                    file.Subtitles ?? "[]"
                ) ?? [];
        }
        catch (JsonException)
        {
            subtitles = [];
        }

        subtitles.RemoveAll(s =>
            string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.Type, DownloadedSubtitleType, StringComparison.OrdinalIgnoreCase)
        );
        subtitles.Add(
            new()
            {
                Language = language,
                Type = DownloadedSubtitleType,
                Ext = SidecarExtension,
            }
        );

        await fileRepository.UpdateVideoFileSubtitlesAsync(
            file.Id,
            JsonConvert.SerializeObject(subtitles),
            ct
        );
    }

    /// <summary>
    /// Resolves the storage the file actually lives on. VideoFile.Share is the folder id and the
    /// folder carries the driver, so a media file on any non-default backend is only reachable
    /// through the factory. The injected IStorageDriver is always LocalStorageDriver, which cannot
    /// see NFS/S3/WebDav-backed media at all: it silently failed the moviehash lookup and threw on
    /// the sidecar write.
    /// </summary>
    private async Task<IStorage?> ResolveStorageAsync(VideoFile file)
    {
        if (!Ulid.TryParse(file.Share, out Ulid folderId))
            return null;

        Folder? folder = await folderRepository.GetFolderByIdAsync(folderId);
        if (folder is null)
            return null;

        return storageFactory.For(folder.Id, folder.DriverId, string.Empty);
    }

    private static (string? Hash, long FileSize) TryComputeHash(IStorage storage, VideoFile file)
    {
        try
        {
            string path = storage.CombinePath(file.HostFolder, file.Filename);
            if (!storage.Exists(path))
                return (null, 0);

            long fileSize = storage.Size(path);
            using Stream stream = storage.OpenRead(path);
            ulong hash = MovieHashHelper.ComputeMovieHash(stream, fileSize);
            return (MovieHashHelper.FormatHash(hash), fileSize);
        }
        catch (Exception)
        {
            // File may be on remote/unreachable storage — fall back to filename/title search.
            return (null, 0);
        }
    }

    private static string[] ResolveLanguages(IQueryCollection query, string fallback)
    {
        if (!query.TryGetValue("language", out StringValues values) || values.Count == 0)
            return [fallback];

        string[] languages = values
            .SelectMany(v =>
                (v ?? string.Empty).Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            )
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return languages.Length > 0 ? languages : [fallback];
    }

    private static SubtitleSearchResultDto MapToDto(SubtitleCandidate candidate) =>
        new(
            Id: candidate.DownloadUrl,
            DownloadUrl: candidate.DownloadUrl,
            Language: candidate.Language,
            LanguageName: Culture.EnglishLanguageName(candidate.Language),
            ReleaseName: candidate.ReleaseName,
            FileName: candidate.FileName,
            Downloads: candidate.Downloads,
            Rating: candidate.Rating,
            Format: candidate.Format,
            HearingImpaired: candidate.HearingImpaired,
            Fps: candidate.Fps,
            Uploader: candidate.Uploader,
            Trusted: candidate.IsTrustedUploader
        );
}
