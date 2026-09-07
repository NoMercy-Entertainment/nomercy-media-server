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

namespace NoMercy.Api.DTOs.Music;

public record TrackAudioAnalysisRequestDto
{
    /// <summary>
    /// Capped so one request cannot ask the server to materialize an entire
    /// library's analysis in a single hop.
    /// </summary>
    public const int MaxTrackIds = 500;

    [JsonProperty("track_ids")]
    public List<Guid> TrackIds { get; set; } = [];
}

public record TrackAudioAnalysisResponseDto
{
    [JsonProperty("data")]
    public List<TrackAudioAnalysisDto> Data { get; set; } = [];
}
