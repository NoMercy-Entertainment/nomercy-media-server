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
/// The facts of one install, written when it happened. Only what cannot be
/// asked again later is kept here: which repository offered the package,
/// whether the official marketplace listed it and whether its published
/// checksum matched. Whether that repository is trusted is not, because the
/// owner can change that at any time and the answer must be the current one.
/// </summary>
public class PluginInstallSource
{
    /// <summary>One of <see cref="PluginInstallSourceKind"/>.</summary>
    public string Kind { get; init; } = PluginInstallSourceKind.Unknown;

    /// <summary>The repository's name as the owner added it. Null for an upload.</summary>
    public string? RepositoryName { get; init; }

    /// <summary>The index URL the entry was read from. Null for an upload.</summary>
    public string? RepositoryUrl { get; init; }

    public bool FromOfficialMarketplace { get; init; }

    /// <summary>Whether a published checksum was checked against the package and matched.</summary>
    public bool ChecksumVerified { get; init; }

    /// <summary>The version this install put in place, from the package's manifest when it had one.</summary>
    public string? Version { get; init; }

    public DateTime RecordedAt { get; init; }
}
