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

using NoMercy.Authorization;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Service.Plugins;

/// <summary>
/// The plugin platform's answer to "who owns this server", read from the user
/// list the rest of the server already keeps.
/// <para>
/// Here rather than in the platform because NoMercy.Plugins does not reference
/// the user list, and should not start to for one property.
/// </para>
/// </summary>
public class PluginOwner(IUserCache userCache) : IPluginOwner
{
    public Guid Id => userCache.Users.FirstOrDefault(user => user.Owner)?.Id ?? Guid.Empty;
}
