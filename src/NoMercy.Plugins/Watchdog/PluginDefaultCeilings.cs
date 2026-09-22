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

namespace NoMercy.PluginSdk.Watchdog;

/// <summary>
/// The same allowance for every plugin until the owner sets one per plugin,
/// which is the quota store this phase adds next.
/// </summary>
public class PluginDefaultCeilings : IPluginResourceCeilingSource
{
    public PluginResourceCeilings For(Ulid pluginId) => PluginResourceCeilings.Default;
}
