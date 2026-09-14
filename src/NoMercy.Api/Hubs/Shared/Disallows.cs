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

namespace NoMercy.Api.Hubs.Shared;

public class Disallows
{
    [JsonProperty("previous")]
    public bool Previous { get; set; }

    [JsonProperty("next")]
    public bool Next { get; set; }

    [JsonProperty("resuming")]
    public bool Resuming { get; set; }

    [JsonProperty("pausing")]
    public bool Pausing { get; set; }

    [JsonProperty("toggling_repeat_context")]
    public bool TogglingRepeatContext { get; set; }

    [JsonProperty("toggling_repeat_track")]
    public bool TogglingRepeatTrack { get; set; }

    [JsonProperty("toggling_shuffle")]
    public bool TogglingShuffle { get; set; }

    [JsonProperty("seeking")]
    public bool Seeking { get; set; }

    [JsonProperty("stopping")]
    public bool Stopping { get; set; }

    [JsonProperty("muting")]
    public bool Muting { get; set; }
}
