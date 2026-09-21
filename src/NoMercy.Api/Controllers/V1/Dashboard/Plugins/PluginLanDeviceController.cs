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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Lan;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// The devices on the owner's network that a plugin serves.
/// <para>
/// Owner only, because adding one hands out a credential that works without
/// signing in. The credential is returned once, when the device is added; a
/// list that read it back would put it in every screenshot of the page.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/lan-devices", Order = 10)]
public class PluginLanDeviceController(
    IPluginManager plugins,
    IPluginLanDeviceStore store,
    PluginLanDeviceMinter minter
) : BaseController
{
    [HttpGet]
    public IActionResult Index(Ulid id)
    {
        if (plugins.GetPluginInfo(id) is null)
            return NotFoundResponse("Plugin not found");

        return Ok(
            new DataResponseDto<IEnumerable<PluginLanDeviceDto>>
            {
                Data = store
                    .For(id)
                    .Select(device => new PluginLanDeviceDto
                    {
                        DeviceId = device.DeviceId,
                        Name = device.Name,
                        CreatedAt = device.CreatedAt,
                        Revoked = device.Revoked,
                    }),
            }
        );
    }

    [HttpPost]
    public IActionResult Store(Ulid id, [FromBody] PluginLanDeviceRequestDto request)
    {
        if (plugins.GetPluginInfo(id) is null)
            return NotFoundResponse("Plugin not found");

        if (string.IsNullOrWhiteSpace(request.Name))
            return UnprocessableEntityResponse(
                "A device needs a name, so you know which one you are revoking later"
            );

        PluginLanDevice device = minter.Mint(id, request.Name.Trim());

        return Ok(
            new DataResponseDto<PluginLanDeviceMintedDto>
            {
                Data = new()
                {
                    DeviceId = device.DeviceId,
                    Name = device.Name,
                    Credential = device.Credential,
                    Url = $"/api/v1/plugins/{id}/lan/{device.DeviceId}/{device.Credential}",
                },
            }
        );
    }

    [HttpDelete("{deviceId}")]
    public IActionResult Destroy(Ulid id, string deviceId)
    {
        if (store.Find(id, deviceId) is null)
            return NotFoundResponse("This plugin has no such device");

        store.Revoke(id, deviceId);

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "The device can no longer reach this plugin",
            }
        );
    }
}
