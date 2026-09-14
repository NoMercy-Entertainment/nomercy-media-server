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
/// Request body for the client network-health report endpoint (REST fallback
/// for clients that don't use the <c>LiveTranscodeHub.ReportBufferHealth</c>
/// SignalR method). Reports the client's download-buffer depth and its
/// measured/estimated downlink so the buffer-adaptive sweep's network axis
/// can react to network conditions independently of encoder-lead.
/// </summary>
public record ReportBufferHealthRequest(
    [property: JsonProperty("buffered_seconds")] double BufferedSeconds,
    [property: JsonProperty("observed_bandwidth_kbps")] double ObservedBandwidthKbps
);
