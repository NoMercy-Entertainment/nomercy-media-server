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

namespace NoMercy.Api.Controllers.V1.Streaming.Dtos;

/// <summary>
/// Response body returned by the buffer-health report endpoint. Echoes the
/// clamped (non-negative) values the server actually recorded.
/// </summary>
public record ReportBufferHealthResponse(
    [property: JsonProperty("buffered_seconds")] double BufferedSeconds,
    [property: JsonProperty("observed_bandwidth_kbps")] double ObservedBandwidthKbps
);
