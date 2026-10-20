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

using NoMercy.Encoder.Profiles;
using LegacyDrmConfig = NoMercy.Encoder.BuildingBlocks.Drm.DrmConfig;
using LegacyDrmMethod = NoMercy.Encoder.BuildingBlocks.Drm.DrmMethod;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// Converts <see cref="DrmConfig"/> into the legacy DRM config shape
/// consumed by the output pipeline. Extracted from PlanStage.
/// </summary>
public static class DrmConfigConverter
{
    public static LegacyDrmConfig? Convert(DrmConfig? v2Drm)
    {
        if (v2Drm is null)
            return null;

        LegacyDrmMethod method = v2Drm.Scheme.ToLowerInvariant() switch
        {
            "aes128" or "aes-128" => LegacyDrmMethod.Aes128,
            "cenc" => LegacyDrmMethod.Cenc,
            _ => LegacyDrmMethod.None,
        };

        string keyUri = DrmKeyParameters.FindKeyUri(v2Drm.Parameters) ?? string.Empty;

        return new(Method: method, KeyUri: keyUri);
    }
}
