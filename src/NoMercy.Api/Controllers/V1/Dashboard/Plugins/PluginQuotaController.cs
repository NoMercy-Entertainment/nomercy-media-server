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

using System.Numerics;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// What one plugin is allowed on this server, and the owner's changes to it.
/// <para>
/// Owner only. An allowance is the owner deciding how much of their own
/// machine somebody else's code may spend, which is not a question a member
/// can answer for them.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/quotas", Order = 10)]
public class PluginQuotaController(
    IPluginManager pluginManager,
    PluginQuotaStore quotas,
    PluginQuotaMeter meter
) : BaseController
{
    [HttpGet]
    public IActionResult Index(Ulid id)
    {
        if (pluginManager.GetPluginInfo(id) is null)
            return NotFoundResponse("Plugin not found");

        return Ok(new DataResponseDto<PluginQuotaDto> { Data = Describe(id) });
    }

    [HttpPut]
    public IActionResult Update(Ulid id, [FromBody] PluginQuotaDto quota)
    {
        if (pluginManager.GetPluginInfo(id) is null)
            return NotFoundResponse("Plugin not found");

        quotas.Save(
            id,
            new(
                Allowance(quota.CpuPercent, double.MaxValue),
                Allowance(quota.MemoryBytes, long.MaxValue),
                Allowance(quota.DiskBytes, long.MaxValue),
                Allowance(quota.UploadBytesPerSecond, long.MaxValue)
            )
        );

        return Ok(new DataResponseDto<PluginQuotaDto> { Data = Describe(id) });
    }

    /// <summary>
    /// Nothing and below is the owner turning a ceiling off. Expressed as one
    /// rule rather than four, because a page that lets an owner clear a field
    /// must mean the same thing in every field.
    /// </summary>
    private static T Allowance<T>(T asked, T unlimited)
        where T : INumber<T> => asked <= T.Zero ? unlimited : asked;

    private PluginQuotaDto Describe(Ulid id)
    {
        PluginQuota quota = quotas.For(id);

        return new()
        {
            CpuPercent = quota.CpuPercent,
            MemoryBytes = quota.MemoryBytes,
            DiskBytes = quota.DiskBytes,
            UploadBytesPerSecond = quota.UploadBytesPerSecond,
            DiskUsedBytes = meter.DiskUsed(id),
            Warning = Warning(quota),
        };
    }

    /// <summary>
    /// Said plainly, because an owner raising a ceiling to unlimited is taking
    /// the server's protection off for that plugin and should read what they
    /// have given up rather than find out from a full disk.
    /// </summary>
    private static string? Warning(PluginQuota quota)
    {
        List<string> off = [];

        if (quota.CpuPercent >= double.MaxValue)
            off.Add("processor");

        if (quota.MemoryBytes == long.MaxValue)
            off.Add("memory");

        if (quota.DiskBytes == long.MaxValue)
            off.Add("disk");

        if (quota.UploadBytesPerSecond == long.MaxValue)
            off.Add("upload");

        if (off.Count == 0)
            return null;

        return $"This plugin has no {string.Join(", ", off)} limit. Nothing stops it taking as much as the server has.";
    }
}
