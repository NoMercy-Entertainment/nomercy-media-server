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

using ImageMagick;

namespace NoMercy.NmSystem.Images;

public interface IImageService
{
    (byte[] data, string mimeType) ResizeMagickNet(
        string image,
        int? width,
        double? aspectRatio,
        string? type,
        int? quality
    );

    MagickFormat Parse(string format);
}
