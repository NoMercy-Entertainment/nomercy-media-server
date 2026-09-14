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

public class VideoCodecDto : CodecDto
{
    [JsonProperty("color_spaces")]
    public LabelValueDto[] AvailableVideoColorSpaces { get; set; } = [];

    [JsonProperty("tunes")]
    public LabelValueDto[] AvailableVideoTunes { get; set; } = [];

    [JsonProperty("profiles")]
    public LabelValueDto[] AvailableVideoProfiles { get; set; } = [];

    [JsonProperty("presets")]
    public LabelValueDto[] AvailablePresets { get; set; } = [];
}
