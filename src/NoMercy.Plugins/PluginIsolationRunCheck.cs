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
using NoMercy.PluginSdk.OutOfProcess;

namespace NoMercy.PluginSdk;

/// <summary>
/// Can it run where the owner put it.
/// <para>
/// Asked only of a plugin set to its own process. That process does not carry
/// everything yet (see <see cref="PluginRemoteReach" />), and a plugin started
/// there without what it uses came up looking healthy and then did nothing.
/// It is refused instead, never moved back into the server: the owner chose
/// the boundary, and crossing it for them is the one thing the loader never
/// does. A plugin in the server's own process is never refused here.
/// </para>
/// </summary>
public sealed class PluginIsolationRunCheck(
    Func<Ulid, PluginIsolation> isolationFor,
    IPluginIsolationFit fit
) : IPluginRunCheck
{
    public PluginRefusal? Check(PluginInfo plugin)
    {
        if (isolationFor(plugin.Id) != PluginIsolation.OutOfProcess)
            return null;

        PluginIsolationVerdict verdict = fit.For(plugin.Capabilities);

        if (verdict.CanRunOutOfProcess)
            return null;

        return new(
            PluginRefusalCodes.OutOfProcessUnsupported,
            $"{plugin.Name} {plugin.Version}",
            "The plugin is set to run in its own process.",
            $"That process cannot serve it: it does not carry {Lost(verdict)}.",
            $"Set this plugin to {nameof(PluginIsolation.InProcess)} on the plugin runtime settings. Docs: {PluginDocs.RuntimeAndIsolation}",
            PluginRefusalSeverity.Blocked
        );
    }

    private static string Lost(PluginIsolationVerdict verdict)
    {
        List<string> parts = [];

        if (verdict.Facades.Count > 0)
            parts.Add($"the facades {string.Join(", ", verdict.Facades)}");

        if (verdict.Hooks.Count > 0)
            parts.Add($"the hooks {string.Join(", ", verdict.Hooks)}");

        if (verdict.Controllers)
            parts.Add("its REST controllers");

        return string.Join("; ", parts);
    }
}
