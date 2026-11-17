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
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Plugins;
using NoMercy.Networking.Http;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Lan;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// The plugin routes that carry no bearer token.
/// <para>
/// A television tuner or an HDHomeRun client cannot sign in. What guards these
/// routes instead is a credential the server minted for one named device, and
/// the fact that they answer only to a device that connected from the owner's
/// own network with no relay in between. Every refusal is the same refusal:
/// a caller from outside, a wrong or revoked credential, a method other than
/// GET, a route the manifest does not name and an address that tries to step
/// out of it all read alike, so nothing tells a caller which part was wrong.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
public class PluginLanController(
    PluginLanDeviceMinter devices,
    IPluginManager plugins,
    PluginLanEndpoints endpoints
) : BaseController
{
    [AllowAnonymous]
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}")]
    public IActionResult Device(Ulid id, string deviceId, string credential)
    {
        // The network first. Answering the credential question to the whole
        // internet would turn this into somewhere to guess at, and a wrong
        // guess from outside should look the same as a wrong guess inside.
        if (!HttpContext.IsDirectLanCaller())
            return Refused(id);

        if (devices.Resolve(id, deviceId, credential) is not { } device)
            return Refused(id);

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

    /// <summary>
    /// A route the plugin's manifest names in <c>capabilities.lan.routes</c>,
    /// served by the plugin's own GET action to the device the credential
    /// belongs to. Every method is routed here so that a method other than
    /// GET gets the same refusal as everything else, rather than a 405 that
    /// says the credential was right.
    /// </summary>
    [AllowAnonymous]
    [ApiExplorerSettings(IgnoreApi = true)]
    [AcceptVerbs(
        "GET",
        "HEAD",
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "OPTIONS",
        Route = "api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}/{**path}"
    )]
    public IActionResult Forward(Ulid id, string deviceId, string credential, string? path)
    {
        if (!HttpContext.IsDirectLanCaller())
            return Refused(id);

        // The credential before anything about the route: without one, a
        // device learns nothing about which routes this plugin serves.
        if (devices.Resolve(id, deviceId, credential) is not { } device)
            return Refused(id);

        if (!HttpMethods.IsGet(Request.Method))
            return Refused(id);

        if (
            string.IsNullOrEmpty(path)
            || !PluginLanRequest.IsPlainRawTarget(
                HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget
            )
        )
            return Refused(id);

        if (
            plugins.GetPluginInfo(id)
            is not { Status: PluginStatus.Active, Capabilities: { Rest: true, Lan: { } lan } }
        )
            return Refused(id);

        if (endpoints.Find(id, lan.Routes, path) is not { } target)
            return Refused(id);

        string version = RouteData.Values["version"]?.ToString() ?? "1";
        string lanPath = $"/api/v{version}/plugins/{id}/lan/{deviceId}/{credential}/";

        if (
            !Uri.TryCreate(
                $"{Request.Scheme}://{Request.Host}{lanPath}",
                UriKind.Absolute,
                out Uri? baseAddress
            )
        )
            return Refused(id);

        // A device the owner revokes while it is still reading is cut off
        // there and then, not when it finishes.
        HttpContext.Response.RegisterForDispose(
            devices
                .RevocationOf(id, deviceId)
                .Register(static state => ((HttpContext)state!).Abort(), HttpContext)
        );

        // Before the plugin runs, so nothing it writes can make the server's
        // response cache keep an answer keyed by this credential.
        PluginLanNoStore.Install(HttpContext);

        PluginLanRequest.Prepare(
            HttpContext,
            new(id, device.DeviceId, device.Name),
            baseAddress,
            $"/api/v{version}/plugins/{id}/{path}"
        );

        return new PluginLanForwardResult(target, version);
    }

    private IActionResult Refused(Ulid pluginId) =>
        ForbiddenResponse(PluginLanRefusal.For(pluginId));
}
