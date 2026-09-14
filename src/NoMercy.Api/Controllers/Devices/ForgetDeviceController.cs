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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Api.WebSockets;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.Controllers.Devices;

[ApiController]
[Authorize]
[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/devices/{deviceId}/forget")]
public sealed class ForgetDeviceController : BaseController
{
    private readonly IDeviceRepository _deviceRepository;
    private readonly IUserCache _userCache;
    private readonly DeviceBusRegistry _registry;

    public ForgetDeviceController(
        IDeviceRepository deviceRepository,
        IUserCache userCache,
        DeviceBusRegistry registry
    )
    {
        _deviceRepository = deviceRepository;
        _userCache = userCache;
        _registry = registry;
    }

    [HttpPost]
    public async Task<IActionResult> Forget(string deviceId)
    {
        User? user = _userCache.GetUser(HttpContext.User.UserId());
        if (user is null)
            return UnauthenticatedResponse("Authentication required.");
        if (!Ulid.TryParse(deviceId, out Ulid id))
            return BadRequestResponse("Invalid device id.");

        Device? device = await _deviceRepository.GetOwnerDeviceAsync(id, user.Id);
        if (device is null)
            return NotFoundResponse("Device not found.");

        Guid ownerUserId = user.Id;

        await _deviceRepository.DeleteDeviceAsync(device);

        // Force-close the WS if still alive (best-effort); device is already gone from DB.
        _registry.ForceClose(id);

        // Broadcast updated device list to all the user's other clients.
        await _registry.BroadcastChange(ownerUserId);

        return NoContent();
    }
}
