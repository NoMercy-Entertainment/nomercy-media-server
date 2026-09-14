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

/// <typeparam name="TEventType">MusicEventType or VideoEventType.</typeparam>
public class BroadcastEventPayload<TEventType>
    where TEventType : struct, Enum
{
    [JsonProperty("deviceBroadcastStatus")]
    public DeviceBroadcastStatus<TEventType> DeviceBroadcastStatus { get; set; } = new();
}

public class DeviceBroadcastStatus<TEventType>
    where TEventType : struct, Enum
{
    [JsonProperty("timestamp")]
    public long Timestamp { get; set; }

    [JsonProperty("broadcast_status")]
    public TEventType BroadcastStatus { get; set; }

    [JsonProperty("device_id")]
    public string DeviceId { get; set; } = null!;
}
