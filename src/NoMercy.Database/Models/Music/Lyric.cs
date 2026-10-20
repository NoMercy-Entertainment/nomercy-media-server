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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Database.Models.Music;

public class Lyric
{
    [JsonProperty("text")]
    public string Text { get; set; } = string.Empty;

    [JsonProperty("time")]
    public LineTime Time { get; set; } = new();

    /// <summary>
    /// Word timings inside the line, in seconds. Absent from the JSON when
    /// null, so rows and clients from before word timing are unaffected.
    /// </summary>
    [JsonProperty("words", NullValueHandling = NullValueHandling.Ignore)]
    public LyricWord[]? Words { get; set; }

    [JsonProperty("rtl")]
    public bool Rtl => Text.GetTextDirection() == StringExtensions.TextDirection.RTL;

    public class LineTime
    {
        [JsonProperty("total")]
        public double Total;

        [JsonProperty("minutes")]
        public int Minutes;

        [JsonProperty("seconds")]
        public int Seconds;

        [JsonProperty("hundredths")]
        public int Hundredths;
    }
}
