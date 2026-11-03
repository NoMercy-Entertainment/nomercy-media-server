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

using Newtonsoft.Json;
using NoMercy.Encoder.Distribution;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

/// <summary>Per-task descriptor in a dispatch request.</summary>
public record DispatchTaskRequest(
    [property: JsonProperty("output_path")] string OutputPath,
    [property: JsonProperty("task_id")] string? TaskId = null,
    [property: JsonProperty("arguments")] List<string>? Arguments = null,
    [property: JsonProperty("task_type")] EncodeTaskType TaskType = EncodeTaskType.QualityVariant,
    /// <summary>Absolute path of the source the task reads; lets a worker fetch
    /// it when it is not local and lets the dispatcher measure it for progress.</summary>
    [property: JsonProperty("input_path")] string? InputPath = null,
    /// <summary>Full source duration, when the caller already measured it.</summary>
    [property: JsonProperty("source_duration_seconds")] double? SourceDurationSeconds = null,
    /// <summary>Start of the slice this task encodes; null for the whole source.</summary>
    [property: JsonProperty("time_range_start_seconds")] double? TimeRangeStartSeconds = null,
    /// <summary>Length of the slice this task encodes; progress is measured
    /// against it instead of the full source.</summary>
    [property: JsonProperty("time_range_duration_seconds")] double? TimeRangeDurationSeconds = null
);
