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

/// <summary>Which index a catalog entry was read from.</summary>
public interface IPluginRepositoryOrigin
{
    /// <summary>
    /// Whether the official marketplace index listed this very entry. By entry,
    /// not by id: two repositories can list the same plugin.
    /// </summary>
    bool IsFromOfficialMarketplace(PluginRepositoryEntry entry);
}
