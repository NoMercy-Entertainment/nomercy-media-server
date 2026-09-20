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
using NoMercy.Plugins.UserData;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// What a plugin holds about one person, handed over or removed.
/// <para>
/// A person may always ask about themselves. Only the owner may ask about
/// somebody else, because on a shared server that is the one account with a
/// reason to: removing what a member left behind after they have gone.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize]
[Route("api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/users/{userId:guid}", Order = 10)]
public class PluginUserDataController(
    IPluginManager plugins,
    IMediaAuthorizationPolicy policy,
    PluginUserDataExporter data
) : BaseController
{
    [HttpGet("export")]
    public async Task<IActionResult> Export(Ulid id, Guid userId, CancellationToken ct)
    {
        if (Refuse(id, userId) is { } refused)
            return refused;

        return Content(await data.ExportAsync(id, userId, ct), "application/json");
    }

    [HttpDelete]
    public IActionResult Destroy(Ulid id, Guid userId)
    {
        if (Refuse(id, userId) is { } refused)
            return refused;

        data.Purge(id, userId);

        return Ok(
            new StatusResponseDto<string>
            {
                Status = "ok",
                Message = "Everything this plugin held about that account is gone",
            }
        );
    }

    private IActionResult? Refuse(Ulid pluginId, Guid userId)
    {
        if (userId != User.UserId() && !policy.IsOwner(User))
            return UnauthorizedResponse("That is not your account");

        if (plugins.GetPluginInfo(pluginId) is null)
            return NotFoundResponse("Plugin not found");

        return null;
    }
}
