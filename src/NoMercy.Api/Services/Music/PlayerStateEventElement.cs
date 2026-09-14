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
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.Services.Music;

public class PlayerStateEventElement
{
    [JsonProperty("event")]
    public PlayerStateEvent Event { get; set; } = null!;

    [JsonProperty("source")]
    public string Source { get; set; } = null!;

    [JsonProperty("type")]
    public MusicEventType Type { get; set; } = MusicEventType.Null;

    [JsonProperty("user")]
    public User User { get; set; } = null!;
}
