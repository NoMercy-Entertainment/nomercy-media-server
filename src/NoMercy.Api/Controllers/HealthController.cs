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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.Setup.Boot;

namespace NoMercy.Api.Controllers;

/// <summary>
/// Health check endpoint for container orchestration and load balancers
/// </summary>
[ApiController]
[Route("[controller]")]
[AllowAnonymous]
public class HealthController(
    MediaContext mediaContext,
    IBootStatus bootStatus,
    IDegradedModeRecovery degradedModeRecovery
) : ControllerBase
{
    /// <summary>
    /// Basic liveness probe — returns 200 if the server process is running
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public IActionResult GetLiveness()
    {
        return Ok(new HealthResponse { Status = "healthy", Timestamp = DateTime.UtcNow });
    }

    /// <summary>
    /// Readiness probe — returns 200 when the server can handle requests, 503 if not ready
    /// </summary>
    [HttpGet("ready")]
    [ProducesResponseType(typeof(ReadinessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReadinessResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetReadiness()
    {
        bool databaseHealthy = await CheckDatabase();

        bool isReady = bootStatus.IsStarted && databaseHealthy;
        string status = isReady ? "ready" : "not_ready";

        ReadinessResponse response = new()
        {
            Status = status,
            Timestamp = DateTime.UtcNow,
            Database = databaseHealthy ? "ok" : "unavailable",
            ServerStarted = bootStatus.IsStarted,
        };

        return isReady
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>
    /// Detailed health check with component status and degraded mode info
    /// </summary>
    [HttpGet("detailed")]
    [ProducesResponseType(typeof(DetailedHealthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DetailedHealthResponse), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDetailed()
    {
        bool databaseHealthy = await CheckDatabase();
        bool isDegraded = Start.IsDegradedMode;

        // Per-component: read the recovery loop's own progress instead of collapsing
        // every deferred dependency onto the one IsDegradedMode flag — a caller could
        // not previously tell "Keycloak unreachable" from "tunnel down" from "not
        // registered yet" because all three read the same boolean.
        RecoveryStatus recoveryStatus = degradedModeRecovery.CurrentStatus;

        string status = DetermineStatus(bootStatus.IsStarted, databaseHealthy, isDegraded);

        DetailedHealthResponse response = new()
        {
            Status = status,
            Timestamp = DateTime.UtcNow,
            Version = Software.GetReleaseVersion(),
            Environment =
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
            UptimeSeconds = (long)(DateTime.UtcNow - Info.StartTime).TotalSeconds,
            Components = new()
            {
                Database = databaseHealthy ? "ok" : "unavailable",
                Authentication = ComponentState(isDegraded, recoveryStatus.Authenticated),
                Network = ComponentState(isDegraded, recoveryStatus.NetworkDiscovered),
                Registration = ComponentState(isDegraded, recoveryStatus.Registered),
            },
            IsDegraded = isDegraded,
        };

        return databaseHealthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }

    /// <summary>
    /// A component is "ok" once the recovery loop reports it recovered even while
    /// <c>IsDegradedMode</c> is still true for the others — this is the per-component
    /// signal the flat boolean couldn't give.
    /// </summary>
    private static string ComponentState(bool isDegraded, bool componentRecovered)
    {
        return !isDegraded || componentRecovered ? "ok" : "degraded";
    }

    private static string DetermineStatus(bool serverStarted, bool databaseHealthy, bool isDegraded)
    {
        if (!serverStarted)
            return "starting";
        if (!databaseHealthy)
            return "unhealthy";
        if (isDegraded)
            return "degraded";
        return "healthy";
    }

    private async Task<bool> CheckDatabase()
    {
        try
        {
            await mediaContext.Database.ExecuteSqlRawAsync("SELECT 1");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
