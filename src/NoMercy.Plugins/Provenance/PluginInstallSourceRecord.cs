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
/// The shape the install sources take in the platform config file. The keys
/// are named apart from the consent, grant and approval records': all of
/// them share one file, and a save replaces a top-level key wholesale.
/// </summary>
public class PluginInstallSourceRecord
{
    /// <summary>
    /// Keyed by the string form of the plugin id, as the consent record is:
    /// the file's Ulid converter cannot write a Ulid as a property name.
    /// </summary>
    public Dictionary<string, PluginInstallSource> InstallSources { get; init; } = [];

    /// <summary>
    /// Updates waiting for the next start, keyed by plugin folder name as the
    /// staged copies themselves are. Compared without case, like the folder.
    /// </summary>
    public Dictionary<string, PluginStagedInstallSource> StagedInstallSources { get; init; } = [];
}
