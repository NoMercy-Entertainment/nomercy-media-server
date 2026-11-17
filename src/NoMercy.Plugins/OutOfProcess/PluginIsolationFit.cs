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
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// Reads <see cref="PluginRemoteReach" /> against a manifest. Decides nothing
/// about loading: it only says what a plugin would lose in another process.
/// </summary>
public sealed class PluginIsolationFit : IPluginIsolationFit
{
    public PluginIsolationVerdict For(PluginCapabilities? capabilities)
    {
        // Asked the way the server's own callers ask, so a manifest with no
        // capabilities block counts as declaring the baseline hooks here too.
        List<string> facades = PluginRemoteReach
            .UncarriedFacades.Where(facade =>
                facade.Value is not null
                && PluginCapabilityGuard.DeclaresHook(capabilities, facade.Value)
            )
            .Select(facade => facade.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        List<string> hooks = PluginRemoteReach
            .EntryPointHooks.Keys.Where(hook =>
                !PluginRemoteReach.ForwardedHooks.Contains(hook)
                && PluginCapabilityGuard.DeclaresHook(capabilities, hook)
            )
            .Order(StringComparer.Ordinal)
            .ToList();

        bool controllers = capabilities?.Rest == true && !PluginRemoteReach.ForwardsControllers;

        return new(facades, hooks, controllers);
    }
}
