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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>Installs an archive a repository offered, knowing which index offered it.</summary>
public interface IPluginRepositoryInstaller
{
    /// <param name="fromOfficialMarketplace">
    /// The official index listed the entry, so its signature has to verify in
    /// every trust mode. Any other repository only warns while enforcing is off.
    /// </param>
    Task InstallFromRepositoryAsync(
        string archivePath,
        string? expectedChecksum,
        PluginIndexSignature? indexSignature,
        bool fromOfficialMarketplace,
        CancellationToken ct = default
    );
}
