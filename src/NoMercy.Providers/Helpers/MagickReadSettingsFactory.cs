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

using System.Drawing;
using ImageMagick;

namespace NoMercy.Providers.Helpers;

/// <summary>
/// Builds the Magick.NET read settings for an image download. A maximum decode
/// size becomes a size hint for the decoder (JPEG scales down while decoding,
/// which keeps big posters from being decoded at full resolution just to be
/// sampled for a color palette).
/// </summary>
public static class MagickReadSettingsFactory
{
    public static MagickReadSettings Create(Size? maxDecodeSize)
    {
        MagickReadSettings settings = new();

        if (maxDecodeSize is not { Width: > 0, Height: > 0 } size)
            return settings;

        settings.Width = (uint)size.Width;
        settings.Height = (uint)size.Height;

        return settings;
    }
}
