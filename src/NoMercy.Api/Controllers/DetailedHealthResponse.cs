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

public record DetailedHealthResponse : HealthResponse
{
    [JsonProperty("version")]
    public required string Version { get; init; }

    [JsonProperty("environment")]
    public required string Environment { get; init; }

    [JsonProperty("uptime_seconds")]
    public required long UptimeSeconds { get; init; }

    [JsonProperty("components")]
    public required ComponentStatus Components { get; init; }

    [JsonProperty("is_degraded")]
    public required bool IsDegraded { get; init; }
}
