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
/// Admin-safe view of a single live session returned by GET /sessions.
/// Does not include internal cancellation tokens or scratch paths.
/// </summary>
public record LiveSessionDto(
    [property: JsonProperty("session_id")] string SessionId,
    [property: JsonProperty("state")] string State,
    [property: JsonProperty("quality_id")] string QualityId,
    [property: JsonProperty("quality_label")] string QualityLabel,
    [property: JsonProperty("width")] int Width,
    [property: JsonProperty("height")] int Height,
    [property: JsonProperty("bitrate_kbps")] int BitrateKbps,
    [property: JsonProperty("position_seconds")] double PositionSeconds,
    [property: JsonProperty("buffer_ahead_seconds")] double BufferAheadSeconds,
    [property: JsonProperty("segment_count")] int SegmentCount,
    [property: JsonProperty("is_complete")] bool IsComplete,
    [property: JsonProperty("last_access")] DateTime LastAccess
);
