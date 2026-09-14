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
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Startup;
using NoMercy.Resources;

namespace NoMercy.Api.Controllers.V1.Encoder;

/// <summary>
/// Point-in-time resource utilization snapshot returned by
/// <c>GET /api/v1/encoder/hardware/utilization</c>.
/// </summary>
public record UtilizationSnapshot(
    [property: JsonProperty("cpu_usage_percent")] double CpuUsagePercent,
    [property: JsonProperty("available_memory_mb")] long AvailableMemoryMb,
    [property: JsonProperty("gpu_samples")] IReadOnlyList<GpuProcessSample> GpuSamples,
    [property: JsonProperty("concurrent_nvenc_sessions")] int ConcurrentNvencSessions,
    [property: JsonProperty("gpus")] IReadOnlyList<GpuDevice> Gpus
);
