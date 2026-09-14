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

/// <summary>
/// Codec + resolution + bitrate of the quality variant chosen for a live session.
/// </summary>
public record SelectedVariantDto(
    [property: JsonProperty("codec")] string Codec,
    [property: JsonProperty("width")] int Width,
    [property: JsonProperty("height")] int Height,
    [property: JsonProperty("bitrate_kbps")] int BitrateKbps
);
