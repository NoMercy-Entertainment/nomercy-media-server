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
using NoMercy.Plugins.Telemetry;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// What this server sends NoMercy about its plugins beyond the refusal counts.
/// <para>
/// Owner only, and off until they say otherwise. Refusal counts are not on
/// this page because they carry nothing about anybody and are how a plugin
/// with a bug gets found; everything that is a choice is here.
/// </para>
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins/telemetry", Order = 10)]
public class PluginTelemetryController : BaseController
{
    [HttpGet]
    public IActionResult Index() =>
        Ok(new DataResponseDto<PluginTelemetryDto> { Data = Describe() });

    [HttpPut]
    public IActionResult Update([FromBody] PluginTelemetryDto choice)
    {
        new PluginTelemetryOptions(choice.ShareCounters).Save();

        return Ok(new DataResponseDto<PluginTelemetryDto> { Data = Describe() });
    }

    private static PluginTelemetryDto Describe() =>
        new() { ShareCounters = PluginTelemetryOptions.Load().ShareCounters };
}
