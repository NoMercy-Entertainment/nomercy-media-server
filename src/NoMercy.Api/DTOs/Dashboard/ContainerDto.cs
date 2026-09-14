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

namespace NoMercy.Api.DTOs.Dashboard;

public record ContainerDto
{
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("value")]
    public string Value { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("default")]
    public bool IsDefault { get; set; }

    [JsonProperty("available_video_codecs")]
    public VideoCodecDto[] AvailableVideoCodecs { get; set; } = [];

    [JsonProperty("available_audio_codecs")]
    public AudioCodecDto[] AvailableAudioCodecs { get; set; } = [];

    [JsonProperty("available_subtitle_codecs")]
    public SubtitleCodecDto[] AvailableSubtitleCodecs { get; set; } = [];

    [JsonProperty("available_resolutions")]
    public VideoQualityDto[] AvailableVideoSizes { get; set; } = [];
}
