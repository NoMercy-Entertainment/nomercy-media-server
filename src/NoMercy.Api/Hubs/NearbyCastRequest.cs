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
    [property: JsonProperty("media_type")] string MediaType,
    [property: JsonProperty("media_id")] string MediaId,
    [property: JsonProperty("resume_at")] int? ResumeAt = null,
    [property: JsonProperty("audio_language")] string? AudioLanguage = null,
    [property: JsonProperty("subtitle_language")] string? SubtitleLanguage = null,
    [property: JsonProperty("subtitle_type")] string? SubtitleType = null,
    [property: JsonProperty("subtitle_codec")] string? SubtitleCodec = null
);
