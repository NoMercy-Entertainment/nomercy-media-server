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
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.Controllers.V1.Music;

/// <summary>
/// Every line time in <see cref="Data" /> is final: the track offset is already
/// applied. <see cref="Offset" /> is informational, for the adjustment UI.
/// <see cref="Source" /> drives attribution of the text; <see cref="Timing" />
/// says where the line timing came from.
/// </summary>
public record LyricsResponseDto
{
    [JsonProperty("data")]
    public Lyric[] Data { get; set; } = [];

    [JsonProperty("offset")]
    public int? Offset { get; set; }

    /// <summary>A <see cref="LyricsSources" /> value, or null for lyrics stored before provenance was recorded.</summary>
    [JsonProperty("source")]
    public string? Source { get; set; }

    /// <summary>A <see cref="LyricsTimings" /> value, or null for lyrics stored before provenance was recorded.</summary>
    [JsonProperty("timing")]
    public string? Timing { get; set; }
}
