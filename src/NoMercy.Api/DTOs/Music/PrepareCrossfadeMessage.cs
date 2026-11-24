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
/// <c>PrepareCrossfade</c>, music hub to a user's clients: the track that
/// comes next, sent once per track ten seconds before the playing one ends,
/// so the player that sounds can load it and fade into it.
/// </summary>
public sealed class PrepareCrossfadeMessage
{
    [JsonProperty("next_item")]
    public required PlaylistTrackDto NextItem { get; init; }

    [JsonProperty("crossfade_duration_ms")]
    public required int CrossfadeDurationMs { get; init; }

    [JsonProperty("timestamp")]
    public required long Timestamp { get; init; }
}
