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

namespace NoMercy.PluginSdk.Revocation;

/// <summary>What must not run, and why: one package, one version, or a whole listing.</summary>
/// <param name="Sha256">The package hash; null with a null Version means the whole listing.</param>
/// <param name="Reason">An i18n key, never a sentence: the clients render it.</param>
public sealed record PluginRevocationEntry(
    Ulid PluginId,
    string? Version,
    string? Sha256,
    string Reason
)
{
    /// <summary>The one match rule: package hash when both sides have one, else version.</summary>
    public bool Matches(Ulid pluginId, string? packageHash, Version installedVersion)
    {
        if (PluginId != pluginId)
            return false;

        if (Sha256 is null && Version is null)
            return true;

        if (Sha256 is not null && !string.IsNullOrWhiteSpace(packageHash))
            return Sha256.Equals(packageHash.Trim(), StringComparison.OrdinalIgnoreCase);

        return Version is not null && SameVersion(Version, installedVersion);
    }

    /// <summary>1.2 and 1.2.0.0 are one version; a pre-release or build label is not compared.</summary>
    private static bool SameVersion(string published, Version installed)
    {
        int label = published.IndexOfAny(['-', '+']);
        string numbers = label < 0 ? published : published[..label];

        return System.Version.TryParse(numbers.Trim(), out Version? revoked)
            && revoked.Major == installed.Major
            && revoked.Minor == installed.Minor
            && Math.Max(revoked.Build, 0) == Math.Max(installed.Build, 0)
            && Math.Max(revoked.Revision, 0) == Math.Max(installed.Revision, 0);
    }
}
