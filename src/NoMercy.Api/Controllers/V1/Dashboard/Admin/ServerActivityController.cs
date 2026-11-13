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

using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services;
using NoMercy.Api.Services.Video;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Server Activity")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/activity", Order = 10)]
public class ServerActivityController(
    IActivityRepository activityRepository,
    IActivityLogger activityLogger,
    IDeviceRepository deviceRepository,
    VideoPlaylistManager videoPlaylistManager,
    PlaybackFailureReportLimiter limiter
) : BaseController
{
    private const int MaxIdLength = 64;
    private const int MaxMessageLength = 500;

    private static readonly string[] ReportableTypes =
    [
        MediaTypes.MovieMediaType,
        MediaTypes.TvMediaType,
        MediaTypes.CollectionMediaType,
        MediaTypes.SpecialMediaType,
    ];

    [HttpGet]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Index([FromQuery] ServerActivityRequest request)
    {
        int take = request.Take ?? 50;
        int skip = request.Skip ?? 0;

        List<ActivityLog> activityLogs = await activityRepository.GetPagedAsync(
            request.Category,
            request.UserId,
            request.DeviceId,
            request.MediaId,
            request.From,
            request.To,
            request.Success,
            skip,
            take
        );

        ServerActivityDto[] activityDtos = activityLogs
            .Select(activityLog => new ServerActivityDto
            {
                Id = activityLog.Id,
                Category = activityLog.Category,
                Type = activityLog.Type,
                Time = activityLog.Time,
                CreatedAt = activityLog.CreatedAt,
                UpdatedAt = activityLog.UpdatedAt,
                // A system event — an encode, a scheduled scan — has no user and no device.
                // The ids stay on the wire as empty rather than null so that clients built
                // against the old shape keep parsing; the names below are what a client
                // actually renders, and blank is how it knows nobody did this.
                UserId = activityLog.UserId ?? Guid.Empty,
                DeviceId = activityLog.DeviceId ?? Ulid.Empty,
                MediaId = activityLog.MediaId,
                Success = activityLog.Success,
                ErrorCode = activityLog.ErrorCode,
                Metadata = activityLog.Metadata,
                Device = activityLog.Device?.Name ?? string.Empty,
                User = activityLog.User?.Name ?? string.Empty,
            })
            .ToArray();

        return Ok(
            new StatusResponseDto<ServerActivityDto[]> { Status = "ok", Data = activityDtos }
        );
    }

    [HttpPost]
    [Authorize(Policy = "MediaAccess")]
    public IActionResult Create()
    {
        return NotImplementedResponse(
            "Activity logs are written by the server, not by client POST."
        );
    }

    /// <summary>A client reports that its own player could not start an item.</summary>
    [HttpPost("playback-failure")]
    [Authorize(Policy = "MediaAccess")]
    public async Task<IActionResult> ReportPlaybackFailure(
        [FromBody] PlaybackFailureReportRequest request,
        [FromQuery] string? deviceId = null
    )
    {
        string? invalidField = InvalidField(request, out int itemId);
        if (invalidField is not null)
            return UnprocessableEntityResponse(
                $"The field '{invalidField}' is missing or invalid."
            );

        Guid userId = User.UserId();
        if (!limiter.TryAcquire(userId))
            return TooManyRequestsResponse(
                $"At most {PlaybackFailureReportLimiter.ReportsPerMinute} playback failure reports per minute."
            );

        // The hubs' own lookup: it only returns items in a library the user can open.
        (VideoPlaylistResponseDto? item, _) = await videoPlaylistManager.GetPlaylist(
            userId,
            request.Type!,
            request.ListId!,
            itemId,
            Language(),
            Country()
        );
        // The lookup falls back to another item of the list; only an exact match counts.
        if (item is null || item.Id != itemId)
            return NotFoundResponse("Item not found.");

        Ulid knownDeviceId = deviceId is { Length: > 0 and <= MaxIdLength }
            ? (await deviceRepository.GetByClientDeviceIdAsync(deviceId))?.Id ?? Ulid.Empty
            : Ulid.Empty;

        await activityLogger.LogFailureAsync(
            "failure.playback_start",
            userId,
            knownDeviceId,
            errorCode: request.ErrorCode!,
            message: $"Playback start failed for {item.Title ?? "<unknown>"} (item {item.Id}): {request.Message ?? request.ErrorCode}"
        );

        return Accepted();
    }

    private static string? InvalidField(PlaybackFailureReportRequest request, out int itemId)
    {
        itemId = 0;

        if (request.Type is null || !ReportableTypes.Contains(request.Type))
            return "type";

        if (request.ListId is not { Length: > 0 and <= MaxIdLength } listId)
            return "list_id";
        bool listIdParses =
            request.Type == MediaTypes.SpecialMediaType
                ? Ulid.TryParse(listId, out _)
                : int.TryParse(listId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        if (!listIdParses)
            return "list_id";

        if (
            request.ItemId is not { Length: > 0 and <= MaxIdLength } rawItemId
            || !int.TryParse(rawItemId, NumberStyles.None, CultureInfo.InvariantCulture, out itemId)
            || itemId <= 0
        )
            return "item_id";

        if (
            request.ErrorCode is not { Length: > 0 and <= MaxIdLength } errorCode
            || !errorCode.All(character =>
                character is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'
            )
        )
            return "error_code";

        if (request.Message is { Length: > MaxMessageLength })
            return "message";

        return null;
    }

    [HttpDelete]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Destroy(
        [FromQuery] ActivityCategory? category,
        [FromQuery] DateTime? before
    )
    {
        int deleted = await activityRepository.DeleteAsync(category, before);
        return Ok(new StatusResponseDto<object> { Status = "ok", Data = new { deleted } });
    }
}
