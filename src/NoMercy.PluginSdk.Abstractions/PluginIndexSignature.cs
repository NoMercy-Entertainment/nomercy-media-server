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

namespace NoMercy.PluginSdk.Abstractions;

// The signature an index published for one version, with every value it
// covers. The values stay as the index wrote them: those characters are signed.
public sealed record PluginIndexSignature(
    string Signature,
    string? KeyId,
    string PluginId,
    string Version,
    string? Sha256,
    string? PublisherId,
    string? Tier,
    IReadOnlyList<string> Capabilities
)
{
    /// <summary>Null when the index carries no signature for this version.</summary>
    public static PluginIndexSignature? From(
        PluginRepositoryEntry entry,
        PluginVersionEntry version
    )
    {
        if (string.IsNullOrWhiteSpace(version.Signature))
            return null;

        return new(
            Signature: version.Signature,
            KeyId: version.KeyId,
            PluginId: entry.Id.ToString(),
            Version: version.Version,
            Sha256: version.Checksum,
            PublisherId: entry.PublisherId,
            Tier: entry.Tier == PluginTier.Paid ? "paid" : "free",
            Capabilities: version.Capabilities ?? []
        );
    }
}
