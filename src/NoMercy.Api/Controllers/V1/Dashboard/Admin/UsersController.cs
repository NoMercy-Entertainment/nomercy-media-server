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
 * Copyright (c) NoMercy Entertainment. All Rights Reserved.
 */

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Users;
using NoMercy.PluginSdk.Guests;
using NoMercy.PluginSdk.UserData;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Users")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/users", Order = 10)]
public class UsersController(
    IUserRepository userRepository,
    IEventBus eventBus,
    PluginGuestInstaller guestInstaller,
    PluginUserDataExporter pluginUserData
) : BaseController
{
    [HttpGet]
    [Authorize(Policy = "Owner")]
    public async Task<IActionResult> Index()
    {
        List<User> users = await userRepository.GetAllWithLibrariesAsync();

        return Ok(
            new DataResponseDto<IEnumerable<PermissionsResponseItemDto>>
            {
                Data = users.Select(user => new PermissionsResponseItemDto(user)),
            }
        );
    }

    [HttpPost]
    [Authorize(Policy = "Owner")]
    public async Task<IActionResult> Store([FromBody] UserRequest request)
    {
        Guid userId = User.UserId();
        User? hasPermission = await userRepository.GetByIdAsync(userId);

        if (hasPermission is null || hasPermission.Owner is false)
            return NotFoundResponse("You do not have permission to create a user");

        bool alreadyExists = await userRepository.ExistsAsync(request.Id);

        if (alreadyExists)
            return UnprocessableEntityResponse("User already exists");

        User newUser = new()
        {
            Id = request.Id,
            Email = request.Email,
            Name = request.Name,
            Allowed = request.Allowed,
            AudioTranscoding = request.AudioTranscoding,
            VideoTranscoding = request.VideoTranscoding,
            NoTranscoding = request.NoTranscoding,
            Manage = request.Manage,
            Owner = request.Owner,
            LibraryUser =
                request
                    .Libraries?.Select(libraryId => new LibraryUser
                    {
                        LibraryId = libraryId,
                        UserId = request.Id,
                    })
                    .ToList()
                ?? [],
        };

        await userRepository.AddAsync(newUser);

        User? createdUser = await userRepository.GetByIdWithLibrariesAfterAddAsync(newUser.Id);

        if (createdUser is null)
            return UnprocessableEntityResponse("User was created but could not be retrieved");

        UserCacheService.AddUser(createdUser);

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "success",
                Message = "User {0} created successfully",
                Data = createdUser.Name,
            }
        );
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Owner")]
    public async Task<IActionResult> Destroy(Guid id, CancellationToken ct)
    {
        User? user = await userRepository.GetByIdWithLibrariesAsync(id);

        if (user is null)
            return NotFoundResponse("User not found");

        if (user.Owner)
            return UnauthorizedResponse("The owner cannot be deleted");

        // Their plugins and everything those plugins held go with them. A
        // guest's install was never the server's, so leaving it behind would
        // keep someone's data on a machine they no longer have an account on.
        await guestInstaller.PurgeForAsync(id, ct);

        // And what every other plugin on this server kept about them. One
        // plugin left holding somebody's history is the same failure as all
        // of them, and the person who left cannot check.
        pluginUserData.PurgeEverywhere(id);

        await userRepository.DeleteAsync(id);

        UserCacheService.RemoveUser(user);

        return Ok(new StatusResponseDto<string> { Status = "success", Message = "User deleted" });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> Show(Guid id)
    {
        User? user = await userRepository.GetByIdWithLibrariesAsync(id);

        if (user is null)
            return NotFoundResponse("User not found");

        return Ok(new DataResponseDto<PermissionsResponseItemDto> { Data = new(user) });
    }

    [HttpGet]
    [Route("permissions")]
    [Authorize(Policy = "Owner")]
    public async Task<IActionResult> PermissionS()
    {
        List<User> users = await userRepository.GetAllWithLibrariesAsync();

        return Ok(
            new DataResponseDto<IEnumerable<PermissionsResponseItemDto>>
            {
                Data = users.Select(user => new PermissionsResponseItemDto(user)),
            }
        );
    }

    [HttpPatch("notifications")]
    public async Task<IActionResult> NotificationS([FromBody] object request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse(
                "You do not have permission to update notification settings"
            );

        User? user = await userRepository.GetByIdWithNotificationsAsync(userId);

        if (user is null)
            return NotFoundResponse("User not found");

        return NotImplementedResponse("Notification settings are not implemented yet.");
    }

    [HttpGet]
    [Route("{id:guid}/permissions")]
    [Authorize(Policy = "Moderator")]
    public async Task<IActionResult> UserPermissions(Guid id)
    {
        if (User.IsSelf(id))
            return UnauthorizedResponse("You do not have permission to edit your own permissions");

        User? user = await userRepository.GetByIdWithLibrariesAsync(id);

        if (user is null)
            return NotFoundResponse("User not found");

        return Ok(new DataResponseDto<UserPermissionRequest> { Data = new(user) });
    }

    [HttpPatch("{id:guid}/permissions")]
    public async Task<IActionResult> UserPermissionUpdate(
        Guid id,
        [FromBody] UserPermissionRequest request
    )
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsModerator(User))
            return UnauthorizedResponse("You do not have permission to update a user");

        if (User.IsSelf(id))
            return UnauthorizedResponse(
                "You do not have permission to update your own permissions"
            );

        User? existing = await userRepository.GetByIdWithLibrariesAsync(id);

        if (existing is null)
            return NotFoundResponse("User not found");

        bool? manage = AuthPolicy.IsOwner(User) ? request.Manage : null;

        await userRepository.UpdatePermissionsAsync(
            targetUserId: id,
            actingUserId: userId,
            allowed: request.Allowed,
            opticalAccess: request.OpticalAccess,
            audioTranscoding: request.AudioTranscoding,
            videoTranscoding: request.VideoTranscoding,
            noTranscoding: request.NoTranscoding,
            manage: manage,
            libraryIds: request.Libraries
        );

        User? updatedUser = await userRepository.GetByIdWithLibrariesAsync(id);

        if (updatedUser is not null)
            UserCacheService.UpdateUser(updatedUser);

        await eventBus.PublishAsync(
            new UserPermissionsChangedEvent { UserId = id, ChangedBy = userId }
        );

        return Ok(new StatusResponseDto<string> { Status = "success", Message = "User updated" });
    }

    [HttpPatch("{id:guid}/notifications")]
    public async Task<IActionResult> UserNotification(Guid id, [FromBody] object request)
    {
        Guid userId = User.UserId();
        if (!AuthPolicy.IsAllowed(User))
            return UnauthorizedResponse(
                "You do not have permission to update notification settings"
            );

        User? user = await userRepository.GetByIdWithNotificationsAsync(userId);

        if (user is null)
            return NotFoundResponse("User not found");

        return NotImplementedResponse("Notification settings are not implemented yet.");
    }
}
