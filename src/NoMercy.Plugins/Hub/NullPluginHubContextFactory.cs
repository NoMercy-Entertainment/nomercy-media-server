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

namespace NoMercy.PluginSdk.Hub;

/// <summary>
/// What a plugin gets where no hub is mapped — the CLI, a unit test, any host
/// that is not the web server.
/// </summary>
public class NullPluginHubContextFactory : IPluginHubContextFactory
{
    public IPluginHubContext For(Ulid pluginId) => new NullPluginHubContext();
}
