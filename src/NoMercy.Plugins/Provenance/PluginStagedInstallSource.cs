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
/// The source of an update staged for the next start. Not the plugin's
/// source yet: the files running now are still the old ones, so it becomes
/// the source only when the staged copy is applied.
/// </summary>
public class PluginStagedInstallSource
{
    public Ulid PluginId { get; init; }

    public PluginInstallSource Source { get; init; } = new();
}
