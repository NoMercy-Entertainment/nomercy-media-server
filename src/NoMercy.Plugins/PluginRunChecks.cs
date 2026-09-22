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
using NoMercy.PluginSdk.Dependencies;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Revocation;

namespace NoMercy.PluginSdk;

/// <summary>
/// Is this exact build still allowed, and has this server heard recently
/// enough to say so.
/// </summary>
public sealed class PluginRevocationRunCheck(
    PluginRevocationGate gate,
    IPluginRevocationStore store
) : IPluginRunCheck
{
    public PluginRefusal? Check(PluginInfo plugin)
    {
        // A server that has never received a list is not a server that
        // stopped receiving one. Nothing serves the list yet, so reading
        // silence as seven days of silence would pause every plugin on every
        // existing server over a feature that does not exist. Phase 3 serves
        // it, and this line goes in the same commit.
        if (store.Current.IssuedAt == DateTimeOffset.MinValue)
            return null;

        return gate.Check(plugin.Id, plugin.AssemblyPath ?? string.Empty);
    }
}

/// <summary>May this owner run it: paid plugins only, and dormant after the grace.</summary>
public sealed class PluginEntitlementRunCheck(PluginEntitlementGate gate) : IPluginRunCheck
{
    public PluginRefusal? Check(PluginInfo plugin) => gate.Check(plugin.Id, plugin.Tier);
}

/// <summary>Is everything it leans on here, paid for and running.</summary>
public sealed class PluginDependencyRunCheck(PluginDependencyGate gate) : IPluginRunCheck
{
    public PluginRefusal? Check(PluginInfo plugin) => gate.Check(plugin);
}

/// <summary>
/// Did the owner say yes to what it asks for.
/// <para>
/// Last on purpose. It is the only one of the four the owner can answer on
/// the spot, so asking it first would send them to a page that changes
/// nothing while the real reason went unsaid.
/// </para>
/// </summary>
public sealed class PluginConsentRunCheck(IPluginConsentService consent) : IPluginRunCheck
{
    public PluginRefusal? Check(PluginInfo plugin)
    {
        if (consent.IsBaseline(plugin.Capabilities))
            return null;

        if (consent.ConsentCoversCapabilities(plugin.Id, plugin.Capabilities, plugin.Version))
            return null;

        return new(
            PluginRefusalCodes.CapabilityNotConsented,
            $"{plugin.Name} {plugin.Version}",
            "The plugin is installed and waiting.",
            "It asks for more than the ordinary set, and nobody has answered yet.",
            "Open the plugin's permissions page and answer what it asks for.",
            PluginRefusalSeverity.Blocked
        );
    }
}
