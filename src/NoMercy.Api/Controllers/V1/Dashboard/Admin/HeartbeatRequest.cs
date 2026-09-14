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

public record HeartbeatRequest(
    [property: JsonProperty("available_cpu_threads")] int AvailableCpuThreads,
    [property: JsonProperty("available_gpu_slots")] int AvailableGpuSlots,
    [property: JsonProperty("gpu_utilization")] double? GpuUtilization = null
);
