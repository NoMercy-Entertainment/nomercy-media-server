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
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media Content Segments")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/content-segments")]
public class ContentSegmentsController(IContentSegmentRepository repository) : BaseController
{
    /// <summary>
    /// Paginated overview of every content segment. Handy for the moderator
    /// dashboard when auditing detector output across the library. Filter by
    /// <c>type</c> (Intro / Outro / Recap / Credits) to narrow.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> List(
        [FromQuery] int pageSize = 100,
        [FromQuery] int pageIndex = 0,
        [FromQuery] ContentSegmentType? type = null
    )
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        if (pageIndex < 0)
            pageIndex = 0;

        List<ContentSegment> segments = await repository.ListAsync(pageSize, pageIndex, type);
        int total = await repository.GetTotalCountAsync();

        return Ok(
            new
            {
                data = segments,
                meta = new
                {
                    total,
                    pageSize,
                    pageIndex,
                    totalPages = (int)Math.Ceiling((double)total / pageSize),
                },
            }
        );
    }

    [HttpGet("episode/{episodeId:int}")]
    [ResponseCache(Duration = 60)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> GetByEpisode(int episodeId)
    {
        List<ContentSegment> segments = await repository.GetForEpisodeAsync(episodeId);
        return Ok(new { data = segments });
    }

    [HttpGet("movie/{movieId:int}")]
    [ResponseCache(Duration = 60)]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> GetByMovie(int movieId)
    {
        List<ContentSegment> segments = await repository.GetForMovieAsync(movieId);
        return Ok(new { data = segments });
    }

    [HttpPost]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Create([FromBody] CreateContentSegmentRequest request)
    {
        if (request.EndSeconds <= request.StartSeconds)
            return BadRequestResponse("end_seconds must be greater than start_seconds");

        if (!request.EpisodeId.HasValue && !request.MovieId.HasValue)
            return BadRequestResponse("Either episode_id or movie_id must be set");

        if (request is { EpisodeId: not null, MovieId: not null })
            return BadRequestResponse("Provide exactly one of episode_id / movie_id, not both");

        ContentSegment segment = new()
        {
            EpisodeId = request.EpisodeId,
            MovieId = request.MovieId,
            SegmentType = request.SegmentType,
            StartSeconds = request.StartSeconds,
            EndSeconds = request.EndSeconds,
            Source = request.Source ?? "manual",
            Confidence = request.Confidence ?? 1.0,
        };

        ContentSegment saved = await repository.CreateAsync(segment);
        return Ok(saved);
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpdateContentSegmentRequest request
    )
    {
        if (!Ulid.TryParse(id, out Ulid segmentId))
            return BadRequestResponse("Invalid segment id");

        ContentSegment? updated = await repository.UpdateAsync(
            segmentId,
            seg =>
            {
                if (request.SegmentType.HasValue)
                    seg.SegmentType = request.SegmentType.Value;
                if (request.StartSeconds.HasValue)
                    seg.StartSeconds = request.StartSeconds.Value;
                if (request.EndSeconds.HasValue)
                    seg.EndSeconds = request.EndSeconds.Value;
                if (request.Confidence.HasValue)
                    seg.Confidence = request.Confidence.Value;
                // Manual edits flip the source so the next detector run
                // doesn't clobber the user's correction.
                seg.Source = "manual";
            }
        );

        if (updated is null)
            return NotFoundResponse("Content segment not found");

        return Ok(updated);
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Ulid.TryParse(id, out Ulid segmentId))
            return BadRequestResponse("Invalid segment id");

        bool deleted = await repository.DeleteAsync(segmentId);
        return deleted ? NoContent() : NotFoundResponse("Content segment not found");
    }
}
