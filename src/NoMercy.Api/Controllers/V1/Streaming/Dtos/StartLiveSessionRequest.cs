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
using NoMercy.Encoder.Codecs;

namespace NoMercy.Api.Controllers.V1.Streaming.Dtos;

public record StartLiveSessionRequest(
    [property: JsonProperty("video_file_id")] string VideoFileId,
    [property: JsonProperty("client_caps")] ClientCapabilitiesDto ClientCaps,
    [property: JsonProperty("start_time_seconds")] double StartTimeSeconds,
    [property: JsonProperty("preferred_quality")] string? PreferredQuality,
    // ISO 639 language of the audio track the viewer picked from the episode's
    // own language list. The encoder maps the matching source stream. Null = let
    // the server default (English, then the file's default track).
    [property: JsonProperty("audio_language")] string? AudioLanguage = null
);
