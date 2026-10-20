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
/// consumed by the output pipeline. Extracted from PlanStage. A scheme the
/// pipeline cannot package fails the plan here rather than converting to
/// DrmMethod.None, which would ship the encode unencrypted.
/// </summary>
public static class DrmConfigConverter
{
    public static LegacyDrmConfig? Convert(DrmConfig? v2Drm)
    {
        if (v2Drm is null)
            return null;

        LegacyDrmMethod method = DrmSchemes.Canonical(v2Drm.Scheme) switch
        {
            DrmSchemes.None => LegacyDrmMethod.None,
            DrmSchemes.Aes128 => LegacyDrmMethod.Aes128,
            DrmSchemes.Cenc => LegacyDrmMethod.Cenc,
            _ => throw new InvalidOperationException(
                $"DRM scheme '{v2Drm.Scheme}' is not supported; supported schemes: "
                    + $"{string.Join(", ", DrmSchemes.Supported)} (or 'none' to disable DRM). "
                    + "Refusing to plan an unencrypted encode for a profile that asked for DRM."
            ),
        };

        string keyUri = DrmKeyParameters.FindKeyUri(v2Drm.Parameters) ?? string.Empty;

        return new(Method: method, KeyUri: keyUri);
    }
}
