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

/*
 * This file is part of the NoMercy Entertainment application.
 * Copyright (c) NoMercy Entertainment. All rights reserved.
 * Licensed under the MIT License.
 */

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.Inbox;
using NoMercy.MediaProcessing.Inbox;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

[ApiController]
[Tags("Dashboard Inbox")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/inbox", Order = 10)]
public class InboxController(
    IInboxRepository inboxRepository,
    IInboxMetadataProbe metadataProbe,
    IEventBus eventBus
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] string? status)
    {
        List<InboxItem> items = await inboxRepository.GetAllAsync(
            status,
            HttpContext.RequestAborted
        );

        return Ok(new { Data = items.Select(item => new InboxItemDto(item)) });
    }

    [HttpGet("{id:ulid}")]
    public async Task<IActionResult> Show(Ulid id)
    {
        InboxItem? item = await inboxRepository.GetByIdAsync(id, HttpContext.RequestAborted);

        if (item is null)
            return NotFoundResponse("Inbox item not found");

        return Ok(new InboxItemDto(item));
    }

    [HttpGet("{id:ulid}/matches")]
    public async Task<IActionResult> Matches(
        Ulid id,
        [FromQuery] string type,
        [FromQuery] string query
    )
    {
        if (string.IsNullOrWhiteSpace(type))
            return BadRequestResponse("type is required");

        if (string.IsNullOrWhiteSpace(query))
            return BadRequestResponse("query is required");

        CandidateMatch[] candidates;

        switch (type)
        {
            case "movie":
                candidates = await metadataProbe.SearchMoviesAsync(
                    query,
                    null,
                    HttpContext.RequestAborted
                );
                break;

            case "tv":
            case "anime":
                candidates = await metadataProbe.SearchTvAsync(
                    query,
                    null,
                    HttpContext.RequestAborted
                );
                break;

            case "music":
                candidates = [];
                break;

            default:
                return BadRequestResponse(
                    $"Unsupported type '{type}'. Expected: movie, tv, anime, music"
                );
        }

        return Ok(new { Data = candidates });
    }

    [HttpPost("{id:ulid}/assign")]
    public async Task<IActionResult> Assign(Ulid id, [FromBody] InboxAssignRequest request)
    {
        InboxItem? item = await inboxRepository.GetTrackedByIdAsync(id, HttpContext.RequestAborted);

        if (item is null)
            return NotFoundResponse("Inbox item not found");

        Folder? folder = await inboxRepository.GetFolderByIdAsync(
            request.TargetFolderId,
            HttpContext.RequestAborted
        );

        if (folder is null)
            return NotFoundResponse("Target folder not found");

        InboxDestination destination = new()
        {
            LibraryId = request.TargetLibraryId,
            FolderId = request.TargetFolderId,
            ProfileId = request.TargetProfileId,
            DriverId = folder.DriverId,
            FolderPath = folder.Path,
        };

        item.DetectedType = request.Type;
        item.TargetLibraryId = request.TargetLibraryId;
        item.TargetFolderId = request.TargetFolderId;
        item.TargetProfileId = request.TargetProfileId;

        try
        {
            await inboxRepository.ExecuteAssignmentAsync(
                item,
                request.Match,
                destination,
                HttpContext.RequestAborted
            );
        }
        catch (Exception ex)
        {
            return InternalServerErrorResponse(
                $"Failed to assign inbox item: {ex.GetType().Name}: {ex.Message}"
            );
        }

        await eventBus.PublishAsync(
            new InboxItemUpdatedEvent { Id = item.Id.ToString(), Status = item.Status }
        );

        return Ok(
            new StatusResponseDto<InboxItemDto>
            {
                Status = "ok",
                Message = "Successfully assigned inbox item.",
                Data = new(item),
            }
        );
    }

    [HttpPost("{id:ulid}/dismiss")]
    public async Task<IActionResult> Dismiss(Ulid id)
    {
        InboxItem? item = await inboxRepository.GetTrackedByIdAsync(id, HttpContext.RequestAborted);

        if (item is null)
            return NotFoundResponse("Inbox item not found");

        await inboxRepository.DismissAsync(item, HttpContext.RequestAborted);

        await eventBus.PublishAsync(
            new InboxItemUpdatedEvent { Id = item.Id.ToString(), Status = item.Status }
        );

        return Ok(
            new StatusResponseDto<string> { Status = "ok", Message = "Inbox item dismissed." }
        );
    }

    [HttpDelete("{id:ulid}")]
    public async Task<IActionResult> Delete(Ulid id)
    {
        InboxItem? item = await inboxRepository.GetTrackedByIdAsync(id, HttpContext.RequestAborted);

        if (item is null)
            return NotFoundResponse("Inbox item not found");

        await inboxRepository.DeleteAsync(item, HttpContext.RequestAborted);

        return Ok(new StatusResponseDto<string> { Status = "ok", Message = "Inbox item removed." });
    }
}
