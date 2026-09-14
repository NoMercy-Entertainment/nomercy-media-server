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
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Distribution;

namespace NoMercy.Api.Controllers.V1.Dashboard.Encoder;

/// <summary>Per-task descriptor in a dispatch request.</summary>
public record DispatchTaskRequest(
    [property: JsonProperty("output_path")] string OutputPath,
    [property: JsonProperty("task_id")] string? TaskId = null,
    [property: JsonProperty("arguments")] List<string>? Arguments = null,
    [property: JsonProperty("task_type")] EncodeTaskType TaskType = EncodeTaskType.QualityVariant
);
