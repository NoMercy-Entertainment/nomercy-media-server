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
using NoMercy.Api.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// What a plugin asks for, and the owner's answer to each.
/// <para>
/// Its own controller rather than more actions on the plugin one, because the
/// existing <c>POST {id}/consent</c> keeps working exactly as it did. An owner
/// on an older client answers all-or-nothing and one on a newer client answers
/// per capability, and neither has to be upgraded first.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/capabilities", Order = 10)]
public class PluginCapabilityController(
    IPluginManager pluginManager,
    IPluginConsentService consentService,
    PluginCapabilityStates states
) : BaseController
{
    [HttpGet]
    public IActionResult Index(Ulid id)
    {
        PluginInfo? plugin = pluginManager.GetPluginInfo(id);

        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        return Ok(
            new DataResponseDto<IEnumerable<PluginCapabilityStateDto>> { Data = states.For(plugin) }
        );
    }

    [HttpPost]
    public IActionResult Store(Ulid id, [FromBody] PluginCapabilityDecisionDto[] decisions)
    {
        PluginInfo? plugin = pluginManager.GetPluginInfo(id);

        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        HashSet<string> declared =
        [
            .. PluginCapabilityStates.Declared(plugin).Select(descriptor => descriptor.Name),
        ];

        // Every name is checked before anything is written. An answer list with
        // one bad name applied halfway would leave the owner having approved a
        // set they never saw.
        string[] unknown =
        [
            .. decisions.Select(decision => decision.Name).Where(name => !declared.Contains(name)),
        ];

        if (unknown.Length > 0)
            return UnprocessableEntityResponse(
                $"The plugin does not ask for: {string.Join(", ", unknown)}"
            );

        foreach (PluginCapabilityDecisionDto decision in decisions)
            if (decision.Approved)
                consentService.ApproveCapability(id, decision.Name, plugin.Version);
            else
                consentService.RevokeCapability(id, decision.Name);

        return Ok(
            new DataResponseDto<IEnumerable<PluginCapabilityStateDto>> { Data = states.For(plugin) }
        );
    }
}
