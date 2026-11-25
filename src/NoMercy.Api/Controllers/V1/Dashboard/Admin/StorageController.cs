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
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Services;
using NoMercy.Authorization;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// What the server's own data uses on disk, a clean-up that runs the nightly
/// maintenance now, and the settings of that maintenance. Owner only; parts
/// are named, no path is returned.
/// </summary>
[ApiController]
[Tags("Dashboard Storage")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/storage", Order = 10)]
public class StorageController(
    IStorageReportService report,
    IStorageCleanupService cleanup,
    IStorageSettingsService settings
) : BaseController
{
    [HttpGet]
    [ProducesResponseType(typeof(StorageReportDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Index()
    {
        StorageReport result = await report.ReadAsync(HttpContext.RequestAborted);
        return Ok(StorageReportDto.From(result));
    }

    [HttpPost]
    [Route("cleanup")]
    [ProducesResponseType(typeof(StorageCleanupResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cleanup()
    {
        StorageCleanupResult? result = await cleanup.RunAsync(HttpContext.RequestAborted);
        return result is null
            ? ConflictResponse("A clean-up or the nightly maintenance is already running.")
            : Ok(StorageCleanupResultDto.From(result));
    }

    [HttpGet]
    [Route("settings")]
    [ProducesResponseType(typeof(StorageSettingsDto), StatusCodes.Status200OK)]
    public IActionResult Settings() => Ok(StorageSettingsDto.From(settings.Read()));

    [HttpPatch]
    [Route("settings")]
    [ProducesResponseType(typeof(StorageSettingsUpdateResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSettings([FromBody] StorageSettingsPatch request)
    {
        StorageSettingsChange change = await settings.ApplyAsync(request, User.UserId());
        if (change.Problem is not null || change.Settings is null)
            return BadRequestResponse(change.Problem ?? "The settings could not be changed.");

        return Ok(
            new StorageSettingsUpdateResponseDto
            {
                Settings = StorageSettingsDto.From(change.Settings),
                RestartRequired = change.RestartRequired,
            }
        );
    }
}
