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
using Newtonsoft.Json;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Jobs;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

public record ProgressUpdateRequest(
    [property: JsonProperty("percent_complete")] double PercentComplete,
    [property: JsonProperty("elapsed_seconds")] double ElapsedSeconds,
    [property: JsonProperty("current_time_seconds")] double CurrentTimeSeconds,
    [property: JsonProperty("duration_seconds")] double DurationSeconds,
    [property: JsonProperty("current_fps")] double? CurrentFps = null,
    [property: JsonProperty("current_speed")] double? CurrentSpeed = null,
    [property: JsonProperty("current_stage")] string? CurrentStage = null,
    [property: JsonProperty("current_operation")] string? CurrentOperation = null,
    [property: JsonProperty("estimated_remaining_seconds")]
        double? EstimatedRemainingSeconds = null,
    [property: JsonProperty("bitrate_kbps")] int? BitrateKbps = null
);
