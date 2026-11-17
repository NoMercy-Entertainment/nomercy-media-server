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
using Microsoft.Extensions.Logging;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Services;
using NoMercy.Data.ServerBackups;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// Backups of the databases and settings, for the server owner only. A restore
/// is verified here and then applied by the next start, before anything opens a
/// database; this endpoint restarts the server to get there.
/// </summary>
[ApiController]
[Tags("Dashboard Server Management")]
[ApiVersion(1.0)]
[Authorize(Policy = "Owner")]
[Route("api/v{version:apiVersion}/dashboard/backups", Order = 10)]
public class BackupsController(
    IBackupService backups,
    IRestoreScheduler restores,
    IServerRestarter serverRestarter,
    ILogger<BackupsController> logger
) : BaseController
{
    public const string ManualReason = "made by the owner";

    [HttpGet]
    [ProducesResponseType(typeof(BackupListResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Index()
    {
        CancellationToken cancellationToken = HttpContext.RequestAborted;
        IReadOnlyList<BackupSetSummary> sets = await backups.ListAsync(cancellationToken);
        PendingRestore? pending = await restores.GetPendingAsync(cancellationToken);
        RestoreResult? last = await restores.GetLastResultAsync(cancellationToken);

        return Ok(
            new BackupListResponseDto
            {
                Backups = sets.Select(BackupSetDto.From).ToList(),
                PendingRestore = pending is null ? null : PendingRestoreDto.From(pending),
                LastRestore = last is null ? null : RestoreResultDto.From(last),
            }
        );
    }

    [HttpPost]
    [ProducesResponseType(typeof(BackupSetDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create()
    {
        try
        {
            BackupSetSummary backup = await backups.CreateAsync(
                ManualReason,
                HttpContext.RequestAborted
            );
            return Ok(BackupSetDto.From(backup));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return InternalServerErrorResponse(
                LogUnexpectedFailure(exception, "The backup could not be made.")
            );
        }
    }

    /// <summary>
    /// Withdraws a restore that is still waiting, for instance one held back
    /// because another server process had the data folder.
    /// </summary>
    [HttpDelete]
    [Route("pending-restore")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelRestore()
    {
        bool withdrawn = await restores.CancelAsync(HttpContext.RequestAborted);
        return withdrawn ? NoContent() : NotFoundResponse("No restore is waiting.");
    }

    [HttpPost]
    [Route("{backupId}/restore")]
    [ProducesResponseType(typeof(RestoreScheduledResponseDto), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Restore(string backupId)
    {
        RestoreScheduleResult result;
        try
        {
            result = await restores.ScheduleAsync(backupId, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return InternalServerErrorResponse(
                LogUnexpectedFailure(exception, "The restore could not be prepared.")
            );
        }

        switch (result.Status)
        {
            case RestoreScheduleStatus.InvalidId:
                return BadRequestResponse("That is not a backup id.");
            case RestoreScheduleStatus.NotFound:
                return NotFoundResponse($"Backup '{backupId}' not found.");
            case RestoreScheduleStatus.AlreadyPending:
                return ConflictResponse("Another restore is already waiting for the restart.");
            case RestoreScheduleStatus.Invalid:
                return UnprocessableEntityResponse(
                    "The backup cannot be restored: " + string.Join(" ", result.Problems)
                );
        }

        ServerRestartOutcome outcome = serverRestarter.Begin();
        if (outcome.Error is not null)
        {
            // A request left behind would restore at some later start, over
            // everything written in between. Withdraw it instead.
            await restores.CancelAsync(CancellationToken.None);
            logger.LogError(
                "Restore withdrawn, the server cannot restart: {Reason}",
                outcome.Error
            );
            return InternalServerErrorResponse(
                $"The server could not restart, so the restore was withdrawn: {outcome.Error}"
            );
        }

        // Stop only after the response is sent, so the caller gets its answer.
        Response.OnCompleted(() =>
        {
            serverRestarter.Complete(outcome);
            return Task.CompletedTask;
        });

        return Accepted(
            new RestoreScheduledResponseDto
            {
                BackupId = backupId,
                SafetyBackupId = result.Pending?.SafetyBackupId,
                Mode = outcome.Mode,
            }
        );
    }
}
