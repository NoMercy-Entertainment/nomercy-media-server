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
using Newtonsoft.Json;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

/// <summary>
/// Cross-library video-file search for the dashboard. Used by every page that
/// needs the user to identify a single file by ULID (content-analysis probes,
/// crop detection, OCR, transcription, on-demand encode tests). Without this
/// the user would be forced to paste a 26-character ULID by hand — there is no
/// dashboard list anywhere that displays those IDs natively.
/// </summary>
[ApiController]
[Tags("Dashboard Server Media Files")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/media/files", Order = 10)]
public class MediaFilesController(IFileRepository fileRepository) : BaseController
{
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? q,
        [FromQuery] int limit = 50,
        CancellationToken ct = default
    )
    {
        // Hard ceiling on `limit` — clients shouldn't be able to pull the whole
        // catalogue through this picker endpoint.
        int safeLimit = Math.Clamp(limit, 1, 200);
        string normalized = (q ?? string.Empty).Trim();

        // BLOCKER: IFileRepository.SearchVideoFilesAsync does not exist yet.
        // Required signature (add to IFileRepository + FileRepository):
        //   Task<List<VideoFile>> SearchVideoFilesAsync(string? query, int limit, CancellationToken ct = default)
        // See MediaFilesController for the exact EF query it must run.
        List<VideoFile> rows = await fileRepository.SearchVideoFilesAsync(
            normalized,
            safeLimit,
            ct
        );

        VideoFileSearchDto[] results = [.. rows.Select(BuildDto)];

        return Ok(new { data = results });
    }

    private static VideoFileSearchDto BuildDto(VideoFile file)
    {
        if (file.Movie is not null)
        {
            return new()
            {
                Id = file.Id.ToString(),
                Type = "movie",
                Label = file.Movie.ReleaseDate.HasValue
                    ? $"{file.Movie.Title} ({file.Movie.ReleaseDate.Value.Year})"
                    : file.Movie.Title,
                ParentLabel = file.Movie.Title,
                Filename = file.Filename,
                Quality = file.Quality,
                Duration = file.Duration,
            };
        }

        if (file.Episode is not null)
        {
            string showTitle = file.Episode.Tv.Title ?? "Unknown show";
            string code = $"S{file.Episode.SeasonNumber:00}E{file.Episode.EpisodeNumber:00}";
            string episodeTitle = file.Episode.Title ?? "Untitled";
            return new()
            {
                Id = file.Id.ToString(),
                Type = "episode",
                Label = $"{showTitle} — {code} — {episodeTitle}",
                ParentLabel = showTitle,
                Filename = file.Filename,
                Quality = file.Quality,
                Duration = file.Duration,
            };
        }

        return new()
        {
            Id = file.Id.ToString(),
            Type = "orphan",
            Label = file.Filename,
            ParentLabel = file.Folder ?? string.Empty,
            Filename = file.Filename,
            Quality = file.Quality,
            Duration = file.Duration,
        };
    }
}
