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
using NoMercy.Api.WebSockets;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Http;
using NoMercy.Networking.Messaging;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[Tags("Dashboard Server Devices")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/devices", Order = 10)]
public class DevicesController(
    IDeviceRepository deviceRepository,
    DeviceBusRegistry busRegistry,
    ConnectedClients connectedClients
) : BaseController
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        List<Device> devices = await deviceRepository.GetDevices();

        DevicesDto[] devicesDtos =
        [
            .. devices.Select(x => new DevicesDto
            {
                Id = x.Id.ToString(),
                DeviceId = x.DeviceId,
                Browser = x.Browser,
                Os = x.Os,
                Device = x.Model,
                Type = x.Type,
                Online =
                    busRegistry.IsOnline(x.Id)
                    || connectedClients.Clients.Values.Any(client => client.Id == x.Id),
                Name = x.Name,
                CustomName = x.CustomName,
                Version = x.Version,
                Ip = x.Ip,
                CreatedAt = x.CreatedAt,
                ActivityLogs = x.ActivityLogs.Select(activityLog => new ActivityLogDto
                {
                    Id = activityLog.Id,
                    Type = activityLog.Type,
                    Time = activityLog.Time,
                    CreatedAt = activityLog.CreatedAt,
                    UserId = activityLog.UserId ?? Guid.Empty,
                    DeviceId = (activityLog.DeviceId ?? Ulid.Empty).ToString(),
                }),
            }),
        ];

        return Ok(new StatusResponseDto<DevicesDto[]> { Status = "ok", Data = devicesDtos });
    }

    [HttpPost]
    public IActionResult Create()
    {
        return NotImplementedResponse("Registering a device from the dashboard is not supported.");
    }

    /// <summary>
    /// "Clear activity logs" — the dashboard/mobile/KMP clients all call this
    /// route to wipe the device activity-log history, not to delete devices.
    /// It used to double as a device-collection delete that silently dragged
    /// every activity log down with it as an undocumented side effect. That
    /// coupling is gone: this route now does exactly the one thing its name
    /// and its callers mean — clear every <see cref="Database.Models.Users.ActivityLog"/>
    /// row — and never removes a <see cref="Device"/> row. Bulk device removal
    /// already has its own honest, narrower endpoints
    /// (<see cref="DestroyOffline"/>, <see cref="DestroyOne"/>).
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Destroy()
    {
        await deviceRepository.DeleteAllActivityLogsAsync();

        return Ok(new StatusResponseDto<object> { Status = "ok", Data = new { } });
    }

    [HttpDelete("offline")]
    public async Task<IActionResult> DestroyOffline()
    {
        List<Device> all = await deviceRepository.GetAllAsync();

        List<Device> offline =
        [
            .. all.Where(d =>
                !busRegistry.IsOnline(d.Id)
                && !connectedClients.Clients.Values.Any(c => c.Id == d.Id)
            ),
        ];

        foreach (Device device in offline)
        {
            busRegistry.ForceClose(device.Id);
            RemoveConnectedClientEntries(device.Id);
            await deviceRepository.DeleteDeviceWithLogsAsync(device.Id);
        }

        foreach (
            Guid ownerId in offline
                .Where(d => d.OwnerUserId is not null)
                .Select(d => d.OwnerUserId!.Value)
                .Distinct()
        )
            await busRegistry.BroadcastChange(ownerId);

        List<Device> remaining = await deviceRepository.GetAllAsync();

        return Ok(
            new StatusResponseDto<object>
            {
                Status = "ok",
                Data = new { removed = offline.Count, devices = remaining },
            }
        );
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DestroyOne(string id)
    {
        if (!Ulid.TryParse(id, out Ulid deviceId))
            return BadRequestResponse("Invalid device id");

        Device? device = await deviceRepository.GetByIdAsync(deviceId);
        if (device is null)
            return NotFoundResponse("Device not found");

        busRegistry.ForceClose(device.Id);
        RemoveConnectedClientEntries(device.Id);

        await deviceRepository.DeleteDeviceWithLogsAsync(device.Id);

        if (device.OwnerUserId is not null)
            await busRegistry.BroadcastChange(device.OwnerUserId.Value);

        List<Device> remaining = await deviceRepository.GetAllAsync();

        return Ok(new StatusResponseDto<object> { Status = "ok", Data = remaining });
    }

    private void RemoveConnectedClientEntries(Ulid deviceId)
    {
        List<string> staleKeys =
        [
            .. connectedClients
                .Clients.Where(pair => pair.Value.Id == deviceId)
                .Select(pair => pair.Key),
        ];

        foreach (string key in staleKeys)
            connectedClients.Clients.TryRemove(key, out Client? _);
    }
}
