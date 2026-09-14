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

public class SubtitleCodecDto : CodecDto
{
    [JsonProperty("available_languages")]
    public LabelValueDto[] AvailableLanguages { get; set; } = [];

    [JsonProperty("hls_segment_filename")]
    public string HlsSegmentFilename { get; set; } = string.Empty;

    [JsonProperty("hls_playlist_filename")]
    public string HlsPlaylistFilename { get; set; } = string.Empty;
}
