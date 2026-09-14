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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Intake;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// Authenticated dashboard surface for configuring intake (drop folder +
/// webhook token). Separate from <c>IntakeWebhookController</c>, which is the
/// anonymous, token-gated machine-to-machine endpoint a render client calls to
/// notify the server of a dropped file.
/// </summary>
[ApiController]
[Tags("Dashboard Intake")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/intake", Order = 10)]
public class IntakeController(
    IIntakeSettings intakeSettings,
    ILibraryRepository libraryRepository,
    ILogger<IntakeController> logger
) : BaseController
{
    private const string WebhookPath = "api/v1/intake/webhook";
    private const string WebhookHeader = "X-Intake-Token";

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        string? dropFolder = await intakeSettings.GetDropFolderAsync(ct);
        bool hasToken = await intakeSettings.HasTokenAsync(ct);

        return Ok(
            new
            {
                dropFolder,
                hasToken,
                webhookPath = WebhookPath,
                webhookHeader = WebhookHeader,
            }
        );
    }

    [HttpPut]
    [Route("drop-folder")]
    public async Task<IActionResult> SetDropFolder(
        [FromBody] SetDropFolderRequest? request,
        CancellationToken ct
    )
    {
        string? path = request?.Path;

        if (string.IsNullOrWhiteSpace(path))
        {
            await intakeSettings.SetDropFolderAsync(null, ct);
            return Ok(new { dropFolder = (string?)null });
        }

        if (await libraryRepository.FindInboxFolderAsync(path, ct) is null)
            return BadRequestResponse(
                "The drop folder must be a folder of an Inbox-type library. Create/point an Inbox library at this folder first."
            );

        await intakeSettings.SetDropFolderAsync(path, ct);

        try
        {
            // Best-effort: a watcher-refresh failure must not fail the settings
            // save — the webhook intake path works regardless of live watch status.
            LibraryFileWatcher.RefreshLibraryCache();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to refresh the library watcher cache after updating the intake drop folder to {DropFolder}",
                path
            );
        }

        return Ok(new { dropFolder = path });
    }

    [HttpPost]
    [Route("token")]
    public async Task<IActionResult> IssueToken(CancellationToken ct)
    {
        string token = await intakeSettings.IssueTokenAsync(ct);

        return Ok(new { token });
    }
}
