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
using NoMercy.Authorization;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
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
    IPluginMediaFactory media
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
}
