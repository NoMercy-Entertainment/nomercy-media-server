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

namespace NoMercy.Encoder.Profiles;

/// <summary>
/// The drm.scheme values the pipeline can package. The profile validator and
/// the plan converter both resolve the scheme through here, so a scheme that
/// passes validation is one the pipeline encrypts with, and one it cannot
/// resolve is rejected in both places instead of becoming "no DRM".
/// </summary>
public static class DrmSchemes
{
    private const string Aes128Hyphenated = "aes-128";

    public const string None = "none";
    public const string Aes128 = "aes128";
    public const string Cenc = "cenc";

    /// <summary>Every spelling a profile may use to ask for encryption.</summary>
    public static readonly IReadOnlyList<string> Supported = [Aes128, Aes128Hyphenated, Cenc];

    /// <summary>
    /// The canonical name for a configured scheme: <see cref="None"/> for an
    /// unset, empty or "none" value, <see cref="Aes128"/> or <see cref="Cenc"/>
    /// for a supported spelling, and null for a scheme the pipeline cannot
    /// package.
    /// </summary>
    public static string? Canonical(string? scheme) =>
        (scheme ?? string.Empty).ToLowerInvariant() switch
        {
            "" or None => None,
            Aes128 or Aes128Hyphenated => Aes128,
            Cenc => Cenc,
            _ => null,
        };
}
