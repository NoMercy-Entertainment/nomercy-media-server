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

// Per-codec shape is the primary contract; the video_codecs/audio_codecs/
// containers/max_width/max_height/supports_10bit fields are optional and only
// populated by older client builds still sending the flat legacy shape (see
// PlaybackDecisionEngine's legacy-payload synthesis, which this DTO feeds).
public record ClientCapabilitiesDto(
    [property: JsonProperty("video")] VideoCodecCapabilityDto[]? Video,
    [property: JsonProperty("audio")] AudioCodecCapabilityDto[]? Audio,
    [property: JsonProperty("supported_containers")] string[]? SupportedContainers,
    [property: JsonProperty("supports_hdr")] bool SupportsHdr,
    [property: JsonProperty("max_bitrate_kbps")] int MaxBitrateKbps,
    [property: JsonProperty("max_audio_channels")] int MaxAudioChannels = 2,
    [property: JsonProperty("video_codecs")] VideoCodecType[]? VideoCodecs = null,
    [property: JsonProperty("audio_codecs")] AudioCodecType[]? AudioCodecs = null,
    [property: JsonProperty("containers")] string[]? Containers = null,
    [property: JsonProperty("max_width")] int? MaxWidth = null,
    [property: JsonProperty("max_height")] int? MaxHeight = null,
    [property: JsonProperty("supports_10bit")] bool? Supports10Bit = null
);
