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
using Microsoft.EntityFrameworkCore;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

// -----------------------------------------------------------------------------
// POST /api/v1/dashboard/notifications/broadcast
// Server owner/moderator sends a one-off message to every user allowed on the
// server. Delivery reuses the existing UserNotifiedEvent -> SignalR "Notify"
// pipeline (SignalRNotificationEventHandler) that every other server-wide
// notice (e.g. RefreshLibrary) already relies on — no new table, no new hub
// method. Reach is therefore real-time only: users without a live connection
// at broadcast time do not receive it retroactively.
//
// POST /api/v1/dashboard/notifications/send
// Same event, scoped to a single user via UserNotifiedEvent.UserId, from where
// NotificationDispatcher picks one transport: the live socket when the user has
// one on the target hub, an encrypted push relayed by nomercy.tv when they do
// not. The "connected" flag reports which of the two it will be, and reads the
// same ConnectedClients registry the SignalR transport delivers through.
// -----------------------------------------------------------------------------

[ApiController]
[Tags("Dashboard Notifications")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/notifications", Order = 10)]
public class NotificationsController(
    IUserRepository userRepository,
    IEventBus eventBus,
    ConnectedClients connectedClients
) : BaseController
{
    private const string DefaultNotificationType = "info";
    private const string DefaultNotificationHub = "videoHub";

    [HttpPost]
    [Route("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastNotificationRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequestResponse("title is required.");

        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequestResponse("body is required.");

        string type = string.IsNullOrWhiteSpace(request.Type)
            ? DefaultNotificationType
            : request.Type;

        int notifiedUsers = await userRepository.CountAllowedAsync();

        await eventBus.PublishAsync(
            new UserNotifiedEvent
            {
                Title = request.Title,
                Message = request.Body,
                Type = type,
            }
        );

        return Ok(
            new DataResponseDto<BroadcastNotificationResponseDto>
            {
                Data = new()
                {
                    NotifiedUsers = notifiedUsers,
                    Title = request.Title,
                    Body = request.Body,
                    Type = type,
                },
            }
        );
    }

    [HttpPost]
    [Route("send")]
    public async Task<IActionResult> Send([FromBody] SendNotificationRequestDto request)
    {
        if (request.UserId == Guid.Empty)
            return BadRequestResponse("user_id is required.");

        if (string.IsNullOrWhiteSpace(request.Title))
            return BadRequestResponse("title is required.");

        if (string.IsNullOrWhiteSpace(request.Body))
            return BadRequestResponse("body is required.");

        User? user = await userRepository.GetByIdAsync(request.UserId);
        if (user is null)
            return NotFoundResponse("User not found.");

        string type = string.IsNullOrWhiteSpace(request.Type)
            ? DefaultNotificationType
            : request.Type;

        bool connected = connectedClients.IsReachable(request.UserId, DefaultNotificationHub);

        await eventBus.PublishAsync(
            new UserNotifiedEvent
            {
                Title = request.Title,
                Message = request.Body,
                Type = type,
                UserId = request.UserId,
                Route = request.Route,
            }
        );

        return Ok(
            new DataResponseDto<SendNotificationResponseDto>
            {
                Data = new()
                {
                    UserId = request.UserId,
                    Title = request.Title,
                    Body = request.Body,
                    Type = type,
                    Connected = connected,
                },
            }
        );
    }
}
