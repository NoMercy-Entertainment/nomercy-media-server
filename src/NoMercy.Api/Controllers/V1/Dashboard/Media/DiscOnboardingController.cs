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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Onboarding;

namespace NoMercy.Api.Controllers.V1.Dashboard.Media;

[ApiController]
[Tags("Dashboard Optical Onboarding")]
[ApiVersion(1.0)]
[Authorize(Policy = "Moderator")]
[Route("api/v{version:apiVersion}/dashboard/optical/onboarding")]
public class DiscOnboardingController(
    IDriveMonitor driveMonitor,
    DiscOnboardingSessionStore store,
    DiscOnboardingOrchestrator orchestrator,
    ILibraryRepository libraryRepository
) : BaseController
{
    /// <summary>
    /// Starts (or restarts) an onboarding session for the disc in
    /// <paramref name="drivePath"/>: probes, identifies, and applies the
    /// auto-confirm rule against <paramref name="libraryId"/>'s
    /// <see cref="Library.AutoConfirmDiscMatches"/>.
    /// </summary>
    [HttpPost("{drivePath}/start")]
    public async Task<IActionResult> StartOnboarding(
        string drivePath,
        [FromQuery] Ulid? libraryId,
        CancellationToken ct
    )
    {
        DiscDrive? drive = FindDrive(drivePath);

        if (drive is null)
            return NotFoundResponse($"No optical drive found at {drivePath}");

        if (!drive.HasDisc)
            return BadRequestResponse($"No disc loaded in drive {drivePath}");

        bool autoConfirmEnabled = false;
        if (libraryId.HasValue)
        {
            Library? library = await libraryRepository.GetLibraryByIdLiteAsync(libraryId.Value, ct);
            autoConfirmEnabled = library?.AutoConfirmDiscMatches ?? false;
        }

        DiscOnboardingSession session = await orchestrator.StartAsync(
            drive,
            autoConfirmEnabled,
            ct
        );

        return Ok(DiscOnboardingStatePayloadDto(session));
    }

    /// <summary>
    /// Applies the user's chosen candidate and dispatches the rip. Body
    /// carries the candidate the user picked from the <c>start</c> response's
    /// <c>candidates</c> array, plus the selected title indices and destination.
    /// </summary>
    [HttpPost("{drivePath}/confirm")]
    public async Task<IActionResult> ConfirmOnboarding(
        string drivePath,
        [FromBody] DiscOnboardingConfirmRequest request,
        CancellationToken ct
    )
    {
        DiscCandidate chosen = new(
            Source: request.Source,
            StableId: request.StableId,
            Title: request.Title,
            Year: request.Year,
            PosterUrl: request.PosterUrl,
            BackdropUrl: null,
            Confidence: 1.0,
            Type: request.MediaType == "tv" ? MediaType.TvShow : MediaType.Movie,
            SeasonNumber: request.SeasonNumber,
            EpisodeNumber: request.EpisodeNumber
        );

        try
        {
            DiscOnboardingSession session = await orchestrator.ConfirmAsync(
                drivePath,
                chosen,
                request.SelectedTitleIndices,
                request.LibraryId,
                request.FolderId,
                ct
            );
            return Ok(DiscOnboardingStatePayloadDto(session));
        }
        catch (InvalidOperationException ex)
        {
            return NotFoundResponse(ex.Message);
        }
    }

    /// <summary>
    /// Poll fallback for clients not yet subscribed to the SignalR broadcast
    /// (e.g. first paint before the hub connection is established).
    /// </summary>
    [HttpGet("{drivePath}")]
    public IActionResult GetOnboardingState(string drivePath)
    {
        if (!store.TryGet(drivePath, out DiscOnboardingSession? session) || session is null)
            return NotFoundResponse($"No active onboarding session for drive {drivePath}");

        return Ok(DiscOnboardingStatePayloadDto(session));
    }

    private DiscDrive? FindDrive(string drivePath) =>
        driveMonitor
            .GetDrives()
            .FirstOrDefault(d =>
                d.Path.TrimEnd('\\', '/')
                    .Equals(drivePath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            );

    private static object DiscOnboardingStatePayloadDto(DiscOnboardingSession session) =>
        new
        {
            session_id = session.SessionId,
            drive_path = session.DrivePath,
            state = session.State.ToString(),
            candidates = session.Candidates,
            job_id = session.JobId,
            failure_reason = session.FailureReason,
            updated_at = session.UpdatedAt,
            result_type = session.ResultType,
            result_id = session.ResultId,
        };
}

public record DiscOnboardingConfirmRequest(
    string Source,
    string StableId,
    string Title,
    string MediaType,
    int[] SelectedTitleIndices,
    Ulid LibraryId,
    Ulid FolderId,
    int? Year = null,
    string? PosterUrl = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null
);
