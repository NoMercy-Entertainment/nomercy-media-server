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

namespace NoMercy.Api.Hubs.Shared;

/// <typeparam name="TState">MusicPlayerState or VideoPlayerState.</typeparam>
/// <typeparam name="TEventType">MusicEventType or VideoEventType.</typeparam>
public class PlayerStateEventElement<TState, TEventType>
    where TState : class
    where TEventType : struct, Enum
{
    [JsonProperty("event")]
    public PlayerStateEvent<TState> Event { get; set; } = null!;

    [JsonProperty("source")]
    public string Source { get; set; } = null!;

    [JsonProperty("type")]
    public TEventType Type { get; set; }

    [JsonProperty("user")]
    public User User { get; set; } = null!;
}

public class PlayerStateEvent<TState>
    where TState : class
{
    [JsonProperty("event_id")]
    public int EventId { get; set; }

    [JsonProperty("state")]
    public TState? State { get; set; }
}
