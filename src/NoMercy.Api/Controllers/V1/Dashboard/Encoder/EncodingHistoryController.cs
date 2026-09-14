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

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

[ApiController]
[Tags("Dashboard Encoding History")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/encoding/history", Order = 10)]
public class EncodingHistoryController(IEncodingHistoryRepository historyRepository)
    : BaseController
{
    /// <summary>
    /// Paginated encoding history. Newest encodes first.
    /// </summary>
    /// <param name="pageSize">Rows per page (1–500, default 50).</param>
    /// <param name="pageIndex">Zero-based page index (default 0).</param>
    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Index(
        [FromQuery] int pageSize = 50,
        [FromQuery] int pageIndex = 0
    )
    {
        pageSize = Math.Clamp(pageSize, 1, 500);
        if (pageIndex < 0)
            pageIndex = 0;

        List<EncodingHistory> entries = await historyRepository.GetRecentAsync(pageSize, pageIndex);
        int total = await historyRepository.GetTotalCountAsync();

        List<EncodingHistoryEntryDto> data = entries
            .Select(e => new EncodingHistoryEntryDto(
                Id: e.Id.ToString(),
                InputPath: e.InputPath,
                OutputPath: e.OutputPath,
                ProfileId: e.ProfileId?.ToString(),
                ProfileName: e.ProfileName,
                EncoderUsed: e.EncoderUsed,
                GpuUsed: e.GpuUsed,
                DurationSeconds: e.DurationSeconds,
                InputSizeBytes: e.InputSizeBytes,
                OutputSizeBytes: e.OutputSizeBytes,
                CompressionRatio: e.CompressionRatio,
                AverageSpeed: e.AverageSpeed,
                AverageFps: e.AverageFps,
                CreatedAt: e.CreatedAt
            ))
            .ToList();

        return Ok(
            new HistoryListResponse(
                Data: data,
                Meta: new(
                    Total: total,
                    PageSize: pageSize,
                    PageIndex: pageIndex,
                    TotalPages: (int)Math.Ceiling((double)total / pageSize)
                )
            )
        );
    }

    /// <summary>
    /// Aggregated stats across every history row (total encodes, bytes
    /// in / out, average speed / fps / compression ratio). One SQL
    /// round-trip, cached for 30 seconds.
    /// </summary>
    [HttpGet("stats")]
    [ResponseCache(Duration = 30)]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Stats()
    {
        EncodingHistoryStats stats = await historyRepository.GetAggregateStatsAsync();
        return Ok(stats);
    }

    /// <summary>
    /// Delete a single history row. Users clean up individual rows from
    /// the dashboard; the encoded output on disk is untouched.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!Ulid.TryParse(id, out Ulid entryId))
            return BadRequestResponse("Invalid history id");

        bool removed = await historyRepository.DeleteAsync(entryId);
        return removed ? NoContent() : NotFoundResponse("History entry not found");
    }

    /// <summary>
    /// Bulk purge. <c>older_than_days</c> drops every row older than N days;
    /// omit it to clear the entire history. Owner-only because clearing the
    /// full history is a coarse change.
    /// </summary>
    [HttpPost("purge")]
    [Authorize(Policy = "Owner")]
    public async Task<IActionResult> Purge([FromBody] PurgeHistoryRequest request)
    {
        int removed = request.OlderThanDays.HasValue
            ? await historyRepository.DeleteOlderThanAsync(
                DateTime.UtcNow.AddDays(-Math.Max(0, request.OlderThanDays.Value))
            )
            : await historyRepository.DeleteAllAsync();

        return Ok(new { removed });
    }
}
