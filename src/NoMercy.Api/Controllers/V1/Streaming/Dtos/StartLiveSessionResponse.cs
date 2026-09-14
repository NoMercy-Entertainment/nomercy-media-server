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

namespace NoMercy.Api.Controllers.V1.Streaming.Dtos;

public record StartLiveSessionResponse(
    [property: JsonProperty("session_id")] string SessionId,
    [property: JsonProperty("playlist_url")] string PlaylistUrl,
    [property: JsonProperty("quality_id")] string QualityId,
    [property: JsonProperty("quality_label")] string QualityLabel
)
{
    /// <summary>
    /// Details of the quality variant selected for this session.
    /// </summary>
    [JsonProperty("selected_variant")]
    public SelectedVariantDto? SelectedVariant { get; init; }

    /// <summary>
    /// UTC timestamp at which this session will be reaped if idle.
    /// </summary>
    [JsonProperty("expires_at")]
    public DateTime? ExpiresAt { get; init; }

    /// <summary>
    /// Indicates how the client should consume this response.
    /// "live" — consume playlist_url via HLS (default, always present for transcode/remux).
    /// "direct" — the file can be played directly; use direct_stream_url instead of playlist_url.
    /// </summary>
    [JsonProperty("mode")]
    public string Mode { get; init; } = "live";

    /// <summary>
    /// Populated when Mode is "direct". The URL served by DynamicStaticFilesMiddleware
    /// at /{hostFolder}/{filename} that the client can feed straight into its player.
    /// Null for live/transcode sessions.
    /// </summary>
    [JsonProperty("direct_stream_url")]
    public string? DirectStreamUrl { get; init; }

    /// <summary>
    /// When Mode is "direct", explains why direct play was chosen.
    /// Null for live/transcode sessions.
    /// </summary>
    [JsonProperty("direct_play_reason")]
    public string? DirectPlayReason { get; init; }
}
