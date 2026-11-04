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
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.DTOs.Media;

public record ScreensaverDataDto
{
    [JsonProperty("aspectRatio")]
    public double AspectRatio { get; set; }

    [JsonProperty("src")]
    public string? Src { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("meta")]
    public MetaDto? Meta { get; set; }

    public ScreensaverDataDto(Image image, Image? logo)
    {
        AspectRatio = image.AspectRatio;
        Src = image.FilePath;
        ColorPalette = image.ColorPalette;
        Meta = new()
        {
            Logo = logo is not null
                ? new LogoDto
                {
                    AspectRatio = logo.AspectRatio,
                    Src = logo.FilePath,
                    ColorPalette = logo.ColorPalette,
                }
                : null,
        };
    }
}
