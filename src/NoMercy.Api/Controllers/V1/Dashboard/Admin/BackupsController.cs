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
using Microsoft.Extensions.Hosting;
using NoMercy.Api.Services;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

[ApiController]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Tags("Dashboard Server Management")]
[Route("api/v{version:apiVersion}/dashboard/backups")]
public sealed class BackupsController(
    IBackupService backups,
    IHostApplicationLifetime applicationLifetime
) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<ActionResult<string>> Create(CancellationToken cancellationToken) =>
        Ok(await backups.CreateAsync(cancellationToken));

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<string>>> List(
        CancellationToken cancellationToken
    ) => Ok(await backups.ListAsync(cancellationToken));

    [HttpPost("{backupId}/restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Restore(string backupId, CancellationToken cancellationToken)
    {
        try
        {
            await backups.RestoreAsync(backupId, cancellationToken);
            applicationLifetime.StopApplication();
            return NoContent();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }
}
