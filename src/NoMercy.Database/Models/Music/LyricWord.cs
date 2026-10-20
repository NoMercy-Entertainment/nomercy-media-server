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

namespace NoMercy.Database.Models.Music;

/// <summary>One timed word of a lyric line. Times are seconds from the start of the track.</summary>
public class LyricWord
{
    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("start")]
    public double Start { get; set; }

    [JsonProperty("end")]
    public double End { get; set; }
}
