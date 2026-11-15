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
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.MediaProcessing.Reclaim;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Reclaim")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/reclaim", Order = 10)]
public class ReclaimController(IReclaimScanService reclaimScanService) : BaseController
{
    private const int DefaultPageSize = 100;
    private const int MinPageSize = 1;
    private const int MaxPageSize = 500;

    [HttpPost]
    [Route("scan")]
    public async Task<IActionResult> Scan()
    {
        if (reclaimScanService.State == ReclaimScanState.Scanning)
            return ConflictResponse("A reclaim scan is already in progress.");

        await reclaimScanService.StartScanAsync(HttpContext.RequestAborted);

        return Ok(
            new
            {
                status = reclaimScanService.State.ToString(),
                lastScannedAt = reclaimScanService.LastScannedAt,
            }
        );
    }

    [HttpGet]
    public IActionResult Index(
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] int pageIndex = 0
    )
    {
        int clampedPageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        int clampedPageIndex = Math.Max(pageIndex, 0);

        ReclaimScanResult? latest = reclaimScanService.Latest;

        if (latest is null)
        {
            return Ok(
                new
                {
                    status = reclaimScanService.State.ToString(),
                    lastScannedAt = reclaimScanService.LastScannedAt,
                    summary = new
                    {
                        totalReclaimableBytes = 0L,
                        itemCount = 0,
                        partialJunkCount = 0,
                        totalPartialJunkBytes = 0L,
                    },
                    items = Array.Empty<ReclaimableItemDto>(),
                }
            );
        }

        long offset = (long)clampedPageIndex * clampedPageSize;

        ReclaimableItemDto[] items =
            offset >= latest.Items.Count
                ? []
                : latest
                    .Items.Skip((int)offset)
                    .Take(clampedPageSize)
                    .Select(item => new ReclaimableItemDto(item))
                    .ToArray();

        return Ok(
            new
            {
                status = reclaimScanService.State.ToString(),
                lastScannedAt = reclaimScanService.LastScannedAt,
                summary = new
                {
                    totalReclaimableBytes = latest.TotalReclaimableBytes,
                    itemCount = latest.Items.Count,
                    partialJunkCount = latest.PartialJunk.Count,
                    totalPartialJunkBytes = latest.TotalPartialJunkBytes,
                },
                items,
            }
        );
    }

    [HttpDelete]
    [Route("items/{id}")]
    public async Task<IActionResult> DeleteItem(string id)
    {
        try
        {
            long freedBytes = await reclaimScanService.DeleteItemAsync(
                id,
                HttpContext.RequestAborted
            );
            return Ok(new { freedBytes });
        }
        catch (KeyNotFoundException)
        {
            return NotFoundResponse($"Reclaimable item '{id}' not found.");
        }
        catch (InvalidOperationException ex)
        {
            return ConflictResponse(ex.Message);
        }
        catch (Exception ex)
        {
            return InternalServerErrorResponse(
                LogUnexpectedFailure(ex, $"Failed to delete reclaimable item '{id}'.")
            );
        }
    }

    [HttpPost]
    [Route("sweep-partials")]
    public async Task<IActionResult> SweepPartials()
    {
        try
        {
            (int count, long bytes) = await reclaimScanService.SweepPartialsAsync(
                HttpContext.RequestAborted
            );

            return Ok(new { count, bytes });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFoundResponse(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ConflictResponse(ex.Message);
        }
        catch (Exception ex)
        {
            return InternalServerErrorResponse(
                LogUnexpectedFailure(ex, "Failed to sweep partial junk.")
            );
        }
    }
}
