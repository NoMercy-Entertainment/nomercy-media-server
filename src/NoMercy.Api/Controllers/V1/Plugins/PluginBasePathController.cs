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
using NoMercy.Api.DTOs.Plugins;
using NoMercy.Api.Plugins;
using NoMercy.Authorization;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Quotas;
using NoMercy.Plugins.Telemetry;
using NoMercy.Plugins.Watchdog;

namespace NoMercy.Api.Controllers.V1.Plugins;

/// <summary>
/// The pages the server owns for every plugin.
/// <para>
/// Every one of them answers the same three questions first: does this person
/// see this plugin at all, is this a page only the owner may open, and does
/// the plugin exist. A page that decided any of those for itself would be a
/// page that could decide differently from its neighbor.
/// </para>
/// </summary>
[ApiController]
[Tags("Plugins")]
[ApiVersion(1.0)]
[Authorize]
public class PluginBasePathController(
    IPluginManager plugins,
    IPluginAccessResolver access,
    IMediaAuthorizationPolicy policy,
    PluginCapabilityStates states,
    IPluginRefusalCounter refusals,
    IPluginCrashCounter crashes,
    PluginWatchdog watchdog,
    PluginQuotaStore quotas,
    PluginQuotaMeter meter
) : BaseController
{
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/info")]
    public IActionResult Info(Ulid id) =>
        Answer(
            id,
            ownerOnly: false,
            plugin => new DataResponseDto<PluginInfoPageDto>
            {
                Data = new()
                {
                    Id = plugin.Id.ToString(),
                    Name = plugin.Name,
                    Description = plugin.Description,
                    Version = plugin.Version.ToString(),
                    Author = plugin.Author,
                    Status = plugin.Status.ToString().ToLowerInvariant(),
                    Sideloaded = plugin.Sideloaded,
                    ProjectUrl = plugin.ProjectUrl,
                },
            }
        );

    /// <summary>
    /// A link, never the fetched page. Rendering documentation an author
    /// controls would put their markup inside the viewer's session.
    /// </summary>
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/docs")]
    public IActionResult Docs(Ulid id) =>
        Answer(
            id,
            ownerOnly: false,
            plugin => new DataResponseDto<PluginDocsPageDto>
            {
                Data = new() { Url = plugin.Docs, ProjectUrl = plugin.ProjectUrl },
            }
        );

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/license")]
    public IActionResult License(Ulid id) =>
        Answer(
            id,
            ownerOnly: false,
            plugin => new DataResponseDto<PluginLicensePageDto>
            {
                Data = new()
                {
                    License = plugin.License,
                    Author = plugin.Author,
                    ProjectUrl = plugin.ProjectUrl,
                },
            }
        );

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/permissions")]
    public IActionResult Permissions(Ulid id) =>
        Answer(
            id,
            ownerOnly: true,
            plugin => new DataResponseDto<PluginPermissionsPageDto>
            {
                Data = new()
                {
                    Isolation = PluginIsolationLevel.Current,
                    NoticeKey = PluginIsolationLevel.NoticeKey,
                    Capabilities = states.For(plugin),
                    Refusals = Counted(plugin.Id),
                },
            }
        );

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/health")]
    public IActionResult Health(Ulid id) =>
        Answer(
            id,
            ownerOnly: true,
            plugin => new DataResponseDto<PluginHealthPageDto>
            {
                Data = new()
                {
                    Status = plugin.Status.ToString().ToLowerInvariant(),
                    Crashes = crashes.CrashesFor(plugin.Id),
                    CeilingHits = crashes.CeilingHitsFor(plugin.Id),
                    Refusals = Counted(plugin.Id),
                    Quota = Allowance(plugin.Id),
                    LastRefusal = watchdog.LastRefusal(plugin.Id) is { } last
                        ? new() { Code = last.Code, Total = 1 }
                        : null,
                },
            }
        );

    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/update")]
    public IActionResult Update(Ulid id) =>
        Answer(
            id,
            ownerOnly: true,
            plugin => new DataResponseDto<PluginUpdatePageDto>
            {
                Data = new()
                {
                    InstalledVersion = plugin.Version.ToString(),
                    // What the repository offers is Phase 3's answer. Said
                    // as "nothing newer" rather than guessed, because a
                    // made-up version is an owner clicking update on air.
                    AvailableVersion = null,
                    UpdateAvailable = false,
                },
            }
        );

    /// <summary>
    /// The owner sees every field. A member sees only the ones the plugin
    /// marked per-user, because a server setting is not theirs to read.
    /// </summary>
    [HttpGet("api/v{version:apiVersion}/plugins/{id:ulid}/_/settings")]
    public IActionResult Settings(Ulid id)
    {
        bool owner = policy.IsOwner(User);

        return Answer(
            id,
            ownerOnly: false,
            plugin => new DataResponseDto<PluginSettingsPageDto>
            {
                Data = new()
                {
                    Scope = owner ? "server" : "user",
                    Fields =
                    [
                        .. plugin
                            .Settings.Where(field =>
                                owner || field.Scope == PluginSettingsScope.User
                            )
                            .Select(Describe),
                    ],
                },
            }
        );
    }

    private static PluginSettingsFieldDto Describe(PluginSettingsField field) =>
        new()
        {
            Key = field.Key,
            LabelKey = field.LabelKey,
            HelpKey = field.HelpKey,
            Type = field.Type,
            Scope = field.Scope.ToString().ToLowerInvariant(),
            Writable = field.Writable,
            // A password lives in the secret store. Echoing one back would put
            // it in every client's memory and every proxy's log.
            Default = field.BackedBySecrets ? null : field.Default,
        };

    private IReadOnlyList<PluginRefusalCountDto> Counted(Ulid id) =>
        [
            .. refusals
                .Counts(id)
                .Select(count => new PluginRefusalCountDto
                {
                    Code = count.Key,
                    Total = count.Value,
                }),
        ];

    private PluginQuotaDto Allowance(Ulid id)
    {
        PluginQuota quota = quotas.For(id);

        return new()
        {
            CpuPercent = quota.CpuPercent,
            MemoryBytes = quota.MemoryBytes,
            DiskBytes = quota.DiskBytes,
            UploadBytesPerSecond = quota.UploadBytesPerSecond,
            DiskUsedBytes = meter.DiskUsed(id),
        };
    }

    /// <summary>
    /// The three questions every page asks, in the order that answers with the
    /// least. A member on an owner-only page is told the same thing as someone
    /// the plugin is not shared with: a refusal that distinguished them would
    /// say which plugins this server has.
    /// </summary>
    private IActionResult Answer(Ulid id, bool ownerOnly, Func<PluginInfo, object> page)
    {
        if (access.Resolve(id, User.UserId()) == PluginAccess.None)
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        if (ownerOnly && !policy.IsOwner(User))
            return ForbiddenResponse(PluginAccessRefusal.For(id));

        if (plugins.GetPluginInfo(id) is not { } plugin)
            return NotFoundResponse("Plugin not found");

        return Ok(page(plugin));
    }
}
