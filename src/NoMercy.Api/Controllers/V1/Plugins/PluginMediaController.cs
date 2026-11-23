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

using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Plugins;
using NoMercy.Authorization;
using NoMercy.Networking.Http;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Lan;
using NoMercy.PluginSdk.Media;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// Serves what a plugin's media ticket points at.
/// <para>
/// The client asks this server, never the provider. The upstream's address and
/// whatever credential it carries stay here, and what the client holds is a
/// link that expires and belongs to one account.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
[Authorize]
public class PluginMediaController(
    PluginMediaTicketMinter minter,
    IPluginAccessResolver access,
    IPluginMediaFactory media,
    PluginLanDeviceMinter devices,
    IPluginManager plugins
) : BaseController
{
    private static readonly string[] Forwarded =
    [
        "Content-Type",
        "Content-Length",
        "Content-Range",
        "Accept-Ranges",
    ];

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/media/{ticket}")]
    public async Task<IActionResult> Media(Ulid id, string ticket, CancellationToken ct)
    {
        Guid userId = User.UserId();

        // The ticket first: it is the cheaper question, and a stale link is
        // what a viewer actually hits rather than a plugin they cannot see.
        if (minter.Refuse(ticket, userId) is { } refused)
            return ForbiddenResponse(refused);

        if (access.Resolve(id, userId) == PluginAccess.None)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        PluginMediaTicket read = minter.Read(ticket)!;

        if (read.PluginId != id)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        // Cleared to play. A live player asks for the same playlist every few
        // seconds, and each ask keeps its ticket good for the next one.
        minter.Played(ticket);

        return await ServeAsync(id, read, ct);
    }

    /// <summary>
    /// What a device on the owner's network fetches through a link minted
    /// while a LAN route served it. No account: the ticket is bound to the
    /// device, it answers only a direct LAN caller, and it stops the moment
    /// the owner revokes the device. Every refusal is the one every LAN
    /// surface gives.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/lan-media/{ticket}")]
    public async Task<IActionResult> LanMedia(Ulid id, string ticket, CancellationToken ct)
    {
        if (!HttpContext.IsDirectLanCaller())
            return ForbiddenResponse(PluginLanRefusal.For(id));

        if (
            minter.ReadForDevice(ticket, id) is not { DeviceId: { } deviceId } read
            || devices.Active(id, deviceId) is not { } device
        )
            return ForbiddenResponse(PluginLanRefusal.For(id));

        if (
            plugins.GetPluginInfo(id)
            is not { Status: PluginStatus.Active, Capabilities.Lan.Routes.Count: > 0 }
        )
            return ForbiddenResponse(PluginLanRefusal.For(id));

        minter.Played(ticket);

        // A device the owner revokes while it is still reading is cut off
        // there and then, not when the channel ends.
        HttpContext.Response.RegisterForDispose(
            devices
                .RevocationOf(id, deviceId)
                .Register(static state => ((HttpContext)state!).Abort(), HttpContext)
        );

        // Asking, for the proxy, is this device: a playlist's children are
        // minted for it rather than refused for having no account.
        HttpContext.User = new(new ClaimsIdentity());
        HttpContext.Features.Set(new PluginLanDispatch(id, device.DeviceId, device.Name));

        // Never kept by a cache: a stored answer would be served again without
        // the network check, the ticket or a revocation being looked at.
        PluginLanNoStore.Install(HttpContext);
        PluginLanNoStore.Seal(Response);

        return await ServeAsync(id, read, ct);
    }

    private async Task<IActionResult> ServeAsync(
        Ulid id,
        PluginMediaTicket read,
        CancellationToken ct
    )
    {
        if (read.FilePath is { } file)
            return ServeFile(id, file);

        // Owned by this request from here on: it is handed out as a stream the
        // client reads until it stops watching, so it cannot be disposed at the
        // end of this method.
        HttpResponseMessage upstream = await media
            .FetcherFor(id)
            .FetchAsync(
                read.Request,
                Request.Headers.Range.ToString() is { Length: > 0 } range ? range : null,
                ct
            );

        HttpContext.Response.RegisterForDispose(upstream);

        foreach (string header in Forwarded)
        {
            if (upstream.Content.Headers.TryGetValues(header, out IEnumerable<string>? values))
                Response.Headers[header] = values.ToArray();
        }

        Response.StatusCode = (int)upstream.StatusCode;

        // Streamed, not buffered: a live stream never finishes, and reading one
        // into memory first is a server that runs out of it.
        return new FileStreamResult(
            await upstream.Content.ReadAsStreamAsync(ct),
            upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
        )
        {
            EnableRangeProcessing = false,
        };
    }

    /// <summary>
    /// A file from the plugin's own folder, served by this server with
    /// seeking. Opened through the folder's guard again: a file that was
    /// swapped for a link since the ticket was minted is refused, and one
    /// that is gone is a 404.
    /// </summary>
    private IActionResult ServeFile(Ulid id, string file)
    {
        Stream? stream;

        try
        {
            stream = media.FetcherFor(id).OpenFile(file);
        }
        catch (PluginRefusedException refused)
        {
            return ForbiddenResponse(refused.Refusal);
        }

        if (stream is null)
            return NotFoundResponse("The file this link was minted for is no longer there.");

        return new FileStreamResult(stream, PluginMediaContentTypes.Of(file))
        {
            EnableRangeProcessing = true,
        };
    }
}
