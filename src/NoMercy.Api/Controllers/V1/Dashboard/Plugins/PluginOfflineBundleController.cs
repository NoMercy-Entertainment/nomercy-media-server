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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Offline;

namespace NoMercy.Api.Controllers.V1.Dashboard.Plugins;

/// <summary>
/// For a server that will never reach NoMercy: the owner turns offline mode
/// on, then carries a signed bundle in from a device that is online.
/// </summary>
[ApiController]
[Tags("Dashboard Server Plugins")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/plugins", Order = 10)]
public class PluginOfflineBundleController(PluginOfflineBundleImporter importer) : BaseController
{
    [HttpGet("offline-mode")]
    public IActionResult Index()
    {
        PluginOfflineMode mode = PluginOfflineMode.Load();

        return Ok(
            new DataResponseDto<PluginOfflineModeDto>
            {
                Data = new()
                {
                    Enabled = mode.Enabled,
                    BundleValidityDays = mode.BundleValidityDays,
                },
            }
        );
    }

    [HttpPost("offline-mode")]
    public IActionResult Store([FromBody] PluginOfflineModeDto request)
    {
        if (request.BundleValidityDays < 1)
            return UnprocessableEntityResponse(
                "A bundle has to be good for at least one day, or importing one does nothing."
            );

        new PluginOfflineMode(request.Enabled, request.BundleValidityDays).Save();

        return Ok(new DataResponseDto<PluginOfflineModeDto> { Data = request });
    }

    [HttpPost("offline-bundle")]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
            return UnprocessableEntityResponse("The file is empty.");

        await using Stream stream = file.OpenReadStream();
        PluginRefusal? refusal = importer.Import(stream);

        // The refusal itself, not a sentence about it: the owner reads the
        // same three lines the server logs, and a client can render the fix
        // without knowing what went wrong.
        if (refusal is not null)
            return UnprocessableEntity(new DataResponseDto<PluginRefusal> { Data = refusal });

        await Task.CompletedTask;

        return Ok(new StatusResponseDto<string> { Status = "ok" });
    }
}
