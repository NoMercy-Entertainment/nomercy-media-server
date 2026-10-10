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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Server Activity")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/activity", Order = 10)]
public class ServerActivityController(IActivityRepository activityRepository) : BaseController
{
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
