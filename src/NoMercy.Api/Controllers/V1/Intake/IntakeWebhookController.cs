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
using NoMercy.Api.DTOs.Intake;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.MediaProcessing.Intake;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Api.Controllers.V1.Intake;

/// <summary>
/// Authenticated inbound intake webhook. A render client (or any authorized
/// producer) POSTs the path of a file it dropped into the configured drop
/// folder; the server re-triggers the existing Inbox pipeline
/// (<see cref="NoMercy.MediaProcessing.EventHandlers.InboxClassifierEventHandler"/>)
/// to pick it up.
///
/// <see cref="AllowAnonymousAttribute"/> is deliberate — this is a
/// machine-to-machine endpoint, not a Keycloak user session. The
/// X-Intake-Token header is the ONLY gate: it is verified with a
/// constant-time comparison inside <see cref="IIntakeSettings"/> and checked
/// before any other work happens. Any failure — missing header, wrong token,
/// or an exception from the verifier — fails closed to 401. The token value
/// itself is never logged.
/// </summary>
[ApiController]
[Tags("Intake")]
[ApiVersion(1.0)]
[AllowAnonymous]
[Route("api/v{version:apiVersion}/intake/webhook")]
public class IntakeWebhookController(
    IIntakeSettings intakeSettings,
    ILibraryRepository libraryRepository,
    IEventBus eventBus
) : BaseController
{
    private const string TokenHeaderName = "X-Intake-Token";

    [HttpPost]
    public async Task<IActionResult> Webhook(
        [FromBody] IntakeWebhookRequest? request,
        CancellationToken ct
    )
    {
        string presentedToken = Request.Headers[TokenHeaderName].ToString();

        bool tokenIsValid;
        try
        {
            tokenIsValid =
                !string.IsNullOrEmpty(presentedToken)
                && await intakeSettings.VerifyTokenAsync(presentedToken, ct);
        }
        catch
        {
            tokenIsValid = false;
        }

        if (!tokenIsValid)
            return UnauthenticatedResponse("Missing or invalid intake token.");

        string? dropFolder = await intakeSettings.GetDropFolderAsync(ct);
        if (string.IsNullOrEmpty(dropFolder))
            return ConflictResponse("No drop folder is configured for intake.");

        if (
            request is null
            || string.IsNullOrWhiteSpace(request.Path)
            || !IsPathUnderRoot(request.Path, dropFolder)
        )
            return BadRequestResponse(
                "path is required and must resolve to a location inside the configured drop folder."
            );

        FolderLibrary? ownedFolder = await libraryRepository.FindInboxFolderAsync(dropFolder, ct);

        if (ownedFolder is null)
            return ConflictResponse(
                "The configured drop folder is not registered as an inbox library."
            );

        await eventBus.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = ownedFolder.Folder.Path,
                LibraryId = ownedFolder.LibraryId,
                LibraryType = MediaTypes.InboxMediaType,
            },
            ct
        );

        return StatusCode(StatusCodes.Status202Accepted, new { status = "accepted" });
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        string fullRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(candidatePath);
        string rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;

        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
