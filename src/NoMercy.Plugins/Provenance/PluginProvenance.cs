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

namespace NoMercy.PluginSdk.Provenance;

/// <summary>
/// Where an installed plugin came from, as the owner should read it now: the
/// facts recorded at install, and the repository's trust as it stands today.
/// <para>
/// A fact that is not known is null rather than false. A plugin installed
/// before sources were recorded did not fail a checksum; nobody knows whether
/// it had one, and saying "not verified" would be a guess.
/// </para>
/// </summary>
public class PluginProvenance
{
    public static PluginProvenance Unknown { get; } = new();

    /// <summary>One of <see cref="PluginInstallSourceKind"/>.</summary>
    public string Kind { get; init; } = PluginInstallSourceKind.Unknown;

    public string? RepositoryName { get; init; }

    public string? RepositoryUrl { get; init; }

    /// <summary>
    /// Whether the repository is trusted on this server right now. Null when
    /// no repository is recorded; false when it was removed since.
    /// </summary>
    public bool? RepositoryTrusted { get; init; }

    /// <summary>Whether the repository is no longer on this server's list. Null when none is recorded.</summary>
    public bool? RepositoryRemoved { get; init; }

    public bool? FromOfficialMarketplace { get; init; }

    public bool? ChecksumVerified { get; init; }

    public string? Version { get; init; }

    public DateTime? RecordedAt { get; init; }
}
