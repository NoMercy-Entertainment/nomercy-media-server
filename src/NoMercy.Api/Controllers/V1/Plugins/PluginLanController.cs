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

using System.Net;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Networking.Http;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Lan;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// The one plugin route that carries no bearer token.
/// <para>
/// A television tuner or an HDHomeRun client cannot sign in. What guards this
/// route instead is a credential the server minted for one named device, and
/// the fact that it answers only to callers on the owner's own network. A
/// request arriving from outside is refused exactly as an unknown credential
/// is, so nothing about this server leaks to whoever found the address.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
public class PluginLanController(PluginLanDeviceMinter devices) : BaseController
{
    [AllowAnonymous]
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}")]
    public IActionResult Device(Ulid id, string deviceId, string credential)
    {
        // The network first. Answering the credential question to the whole
        // internet would turn this into somewhere to guess at, and a wrong
        // guess from outside should look the same as a wrong guess inside.
        if (!ClientIpResolver.IsPrivateNetwork(HttpContext.ClientIp()))
            return ForbiddenResponse(Refusal(id));

        if (devices.Refuse(id, deviceId, credential) is { } refused)
            return ForbiddenResponse(refused);

        PluginLanDevice device = devices.Resolve(id, deviceId, credential)!;

        return Ok(
            new DataResponseDto<PluginLanDeviceDto>
            {
                Data = new()
                {
                    DeviceId = device.DeviceId,
                    Name = device.Name,
                    CreatedAt = device.CreatedAt,
                    Revoked = device.Revoked,
                },
            }
        );
    }

    private static PluginRefusal Refusal(Ulid pluginId) =>
        new(
            PluginRefusalCodes.LanCredentialInvalid,
            pluginId.ToString(),
            "The device could not reach the plugin.",
            "This address answers only to devices on the same network as the server.",
            "Use the address from the plugin's page, on a device in the same house as the server.",
            PluginRefusalSeverity.Blocked
        );
}
