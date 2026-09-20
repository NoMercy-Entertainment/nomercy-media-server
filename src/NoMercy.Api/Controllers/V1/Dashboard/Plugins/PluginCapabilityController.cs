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
    IPluginConsentService consentService
) : BaseController
{
    [HttpGet]
    public IActionResult Index(Ulid id)
    {
        PluginInfo? plugin = pluginManager.GetPluginInfo(id);

        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        return Ok(
            new DataResponseDto<IEnumerable<PluginCapabilityStateDto>>
            {
                Data = Declared(plugin).Select(descriptor => Describe(id, descriptor)),
            }
        );
    }

    [HttpPost]
    public IActionResult Store(Ulid id, [FromBody] PluginCapabilityDecisionDto[] decisions)
    {
        PluginInfo? plugin = pluginManager.GetPluginInfo(id);

        if (plugin is null)
            return NotFoundResponse("Plugin not found");

        HashSet<string> declared = [.. Declared(plugin).Select(descriptor => descriptor.Name)];

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
            new DataResponseDto<IEnumerable<PluginCapabilityStateDto>>
            {
                Data = Declared(plugin).Select(descriptor => Describe(id, descriptor)),
            }
        );
    }

    /// <summary>
    /// The capabilities this plugin's manifest declares, as the vocabulary
    /// describes them. A hook the vocabulary does not carry is skipped rather
    /// than shown: the owner cannot meaningfully answer for something the
    /// server has no description of.
    /// </summary>
    private static IEnumerable<PluginCapabilityDescriptor> Declared(PluginInfo plugin) =>
        (plugin.Capabilities?.Hooks ?? [])
            .Select(PluginCapabilityVocabulary.ByName)
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!);

    private PluginCapabilityStateDto Describe(Ulid id, PluginCapabilityDescriptor descriptor) =>
        new()
        {
            Name = descriptor.Name,
            SummaryKey = descriptor.Summary,
            Trust = descriptor.Trust.ToString().ToLowerInvariant(),
            DocsUrl = descriptor.DocsUrl,
            Approved = consentService.IsApproved(id, descriptor.Name),
            ApprovedAtVersion = consentService.ApprovedAt(id, descriptor.Name)?.ToString(),
        };
}
