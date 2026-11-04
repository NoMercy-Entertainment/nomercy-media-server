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
using NoMercy.Database;

namespace NoMercy.Api.DTOs.Media;

public record LogoDto
{
    [JsonProperty("aspectRatio")]
    public double AspectRatio { get; set; }

    [JsonProperty("src")]
    public string Src { get; set; } = string.Empty;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }
}
