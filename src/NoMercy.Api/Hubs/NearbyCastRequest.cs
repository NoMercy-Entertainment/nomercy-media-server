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

namespace NoMercy.Api.Hubs;

/// <summary>What to play on a nearby Cast device; same fields the cast LAUNCH intent carries.</summary>
public sealed record NearbyCastRequest(
    [property: JsonProperty("media_type")] string? MediaType = null,
    [property: JsonProperty("media_id")] string? MediaId = null,
    [property: JsonProperty("resume_at")] int? ResumeAt = null,
    [property: JsonProperty("audio_language")] string? AudioLanguage = null,
    [property: JsonProperty("subtitle_language")] string? SubtitleLanguage = null,
    [property: JsonProperty("subtitle_type")] string? SubtitleType = null,
    [property: JsonProperty("subtitle_codec")] string? SubtitleCodec = null,
    [property: JsonProperty("list_type", NullValueHandling = NullValueHandling.Ignore)] string? ListType = null,
    [property: JsonProperty("list_id", NullValueHandling = NullValueHandling.Ignore)] string? ListId = null,
    [property: JsonProperty("track_id", NullValueHandling = NullValueHandling.Ignore)] string? TrackId = null,
    [property: JsonProperty("season", NullValueHandling = NullValueHandling.Ignore)] int? Season = null,
    [property: JsonProperty("episode", NullValueHandling = NullValueHandling.Ignore)] int? Episode = null
);
