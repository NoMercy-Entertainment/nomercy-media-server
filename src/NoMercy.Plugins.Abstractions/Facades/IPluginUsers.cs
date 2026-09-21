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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Everyone on this server. Owner-only, and it answers identities rather than
/// accounts: a plugin that needs to list members does not need their addresses.
/// </summary>
public interface IPluginUsers
{
    Task<IReadOnlyList<PluginUserIdentity>> ListAsync(CancellationToken ct = default);
}
