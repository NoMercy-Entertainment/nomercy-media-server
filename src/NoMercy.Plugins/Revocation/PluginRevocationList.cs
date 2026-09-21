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

namespace NoMercy.Plugins.Revocation;

/// <summary>Every build this server knows was revoked, and when it last heard.</summary>
public sealed record PluginRevocationList(
    DateTimeOffset IssuedAt,
    IReadOnlyList<PluginRevocationEntry> Entries
)
{
    /// <summary>A server that has never reached nomercy.tv. Reads as infinitely stale.</summary>
    public static PluginRevocationList None { get; } = new(DateTimeOffset.MinValue, []);

    public PluginRevocationEntry? Find(Ulid pluginId, string packageHash) =>
        Entries.FirstOrDefault(entry =>
            entry.PluginId == pluginId
            && entry.Hash.Equals(packageHash, StringComparison.OrdinalIgnoreCase)
        );
}
