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

using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Sources;

namespace NoMercy.OpticalMedia.Rip;

/// <summary>
/// Validates and enriches a rip request before a <see cref="DiscRipJob"/> is built: fails
/// fast on a DRM-locked disc, validates the destination folder/library for
/// <see cref="RipMode.RipAndEncode"/>, stamps the resolved <see cref="OpticalDiscType"/> onto
/// the request, defaults a CD rip's title selection to every probed audio track, and ensures
/// the output directory exists.
/// </summary>
public interface IDiscRipPreparationService
{
    Task<DiscRipPreparationResult> PrepareAsync(
        DiscDrive drive,
        RipRequest request,
        CancellationToken ct
    );
}
