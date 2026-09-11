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

/// <summary>
/// One place in a track a transition could sensibly start or land, as the
/// automix plugin's DJ analysis found it.
/// </summary>
public record CuePointDto
{
    [JsonProperty("ms")]
    public int Ms { get; set; }

    /// <summary>One of "intro", "drop", "breakdown" or "outro".</summary>
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>One of "mixIn" or "mixOut".</summary>
    [JsonProperty("direction")]
    public string Direction { get; set; } = string.Empty;

    /// <summary>0..1, how strong a candidate this is relative to the track's other cue points.</summary>
    [JsonProperty("score")]
    public double Score { get; set; }
}
