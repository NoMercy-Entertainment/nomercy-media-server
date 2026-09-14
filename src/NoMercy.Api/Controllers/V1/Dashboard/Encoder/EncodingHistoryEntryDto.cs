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
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

/// <summary>
/// Frontend-facing shape for a single history row. Snake_case via
/// JsonProperty matches the rest of the dashboard API surface.
/// </summary>
public record EncodingHistoryEntryDto(
    [property: JsonProperty("id")] string Id,
    [property: JsonProperty("input_path")] string InputPath,
    [property: JsonProperty("output_path")] string OutputPath,
    [property: JsonProperty("profile_id")] string? ProfileId,
    [property: JsonProperty("profile_name")] string ProfileName,
    [property: JsonProperty("encoder_used")] string EncoderUsed,
    [property: JsonProperty("gpu_used")] string? GpuUsed,
    [property: JsonProperty("duration_seconds")] double DurationSeconds,
    [property: JsonProperty("input_size_bytes")] long InputSizeBytes,
    [property: JsonProperty("output_size_bytes")] long OutputSizeBytes,
    [property: JsonProperty("compression_ratio")] double CompressionRatio,
    [property: JsonProperty("average_speed")] double AverageSpeed,
    [property: JsonProperty("average_fps")] double AverageFps,
    [property: JsonProperty("created_at")] DateTime CreatedAt
);
