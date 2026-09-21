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
using NoMercy.Authorization;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Media;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// The channels and guide a plugin published, and a playable link for one.
/// <para>
/// A channel's own addresses never appear here. Asking for a channel mints a
/// ticket on this server for whoever asked, which is what the player is given.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
[Authorize]
public class PluginLiveController(
    IPluginLiveStore store,
    IPluginAccessResolver access,
    PluginMediaTicketMinter minter
) : BaseController
{
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/live")]
    public IActionResult Channels(Ulid id)
    {
        Guid userId = User.UserId();

        if (access.Resolve(id, userId) == PluginAccess.None)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        return Ok(
            new DataResponseDto<object>
            {
                Data = new
                {
                    Channels = store
                        .Channels(id)
                        .Select(channel => Describe(id, channel, userId))
                        .ToList(),
                    Groups = store.Groups(id),
                },
            }
        );
    }

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/live/{channel}/guide")]
    public IActionResult Guide(Ulid id, string channel, [FromQuery] DateTimeOffset? at)
    {
        Guid userId = User.UserId();

        if (access.Resolve(id, userId) == PluginAccess.None)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        return Ok(
            new DataResponseDto<IEnumerable<PluginEpgProgram>>
            {
                Data = store.ProgramsAt(id, channel, at ?? DateTimeOffset.UtcNow),
            }
        );
    }

    /// <summary>
    /// A channel as a client sees it: everything the provider said about it,
    /// and one link on this server in place of the addresses behind it.
    /// </summary>
    private object Describe(Ulid pluginId, PluginLiveChannel channel, Guid userId) =>
        new
        {
            channel.Id,
            channel.Name,
            channel.Number,
            channel.Group,
            channel.Logo,
            channel.Radio,
            channel.Adult,
            channel.AgeRating,
            Kind = channel.StreamKind.ToString().ToLowerInvariant(),
            Url = $"/api/v1/plugins/{pluginId}/media/"
                + minter.Mint(
                    pluginId,
                    userId,
                    new PluginProxyRequest { Links = channel.Links },
                    PluginMediaProxy.TicketLifetime
                ),
        };
}
