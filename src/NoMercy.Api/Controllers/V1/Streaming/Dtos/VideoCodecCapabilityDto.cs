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

public record VideoCodecCapabilityDto(
    [property: JsonProperty("codec")] VideoCodecType Codec,
    [property: JsonProperty("profiles")] string[] Profiles,
    [property: JsonProperty("max_bit_depth")] int MaxBitDepth,
    [property: JsonProperty("max_width")] int MaxWidth,
    [property: JsonProperty("max_height")] int MaxHeight,
    [property: JsonProperty("max_framerate")] int MaxFramerate,
    [property: JsonProperty("hdr_formats")] string[] HdrFormats,
    [property: JsonProperty("max_bitrate_kbps")] int MaxBitrateKbps
);
