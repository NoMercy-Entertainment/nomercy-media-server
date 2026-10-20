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
/// The drm.parameters entries that may carry the key delivery address. The
/// profile validator and the plan converter both resolve the key through
/// here, so a profile that passes validation is one the packager can read.
/// </summary>
public static class DrmKeyParameters
{
    /// <summary>
    /// Accepted parameter names in order of precedence; matched without regard
    /// to letter case.
    /// </summary>
    public static readonly IReadOnlyList<string> AcceptedNames =
    [
        "key_uri",
        "key_url",
        "license_url",
        "keyfile",
        "key_file",
    ];

    /// <summary>
    /// The first non-blank value stored under an accepted name, or null when
    /// the parameters carry no usable key address.
    /// </summary>
    public static string? FindKeyUri(IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
            return null;

        foreach (string name in AcceptedNames)
        {
            foreach ((string key, string value) in parameters)
            {
                if (
                    key.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(value)
                )
                    return value;
            }
        }

        return null;
    }
}
