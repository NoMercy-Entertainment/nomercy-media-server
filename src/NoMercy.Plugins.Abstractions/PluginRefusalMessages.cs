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

/// <summary>The refusals the host raises often, written once so the wording does not drift.</summary>
public static class PluginRefusalMessages
{
    public static PluginRefusal HostServicesRemoved(string plugin, string service)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostServicesRemoved,
            plugin,
            $"The plugin asked the host container for {service}.",
            "Contract v3 has no host container. Every route into the server is a facade on IPluginContext, so the owner can see and revoke it.",
            "Use context.Metadata.QueryAsync (capability metadata.query). Docs: /nomercy-plugins/capabilities/metadata-query",
            PluginRefusalSeverity.Blocked
        );
    }

    /// <summary>
    /// A plugin compiled against contract v2 calling a member v3 took away.
    /// <para>
    /// The runtime raises this the first time the method runs, not at load, so
    /// the plugin installs and enables and then fails on one route. Naming the
    /// member is the whole value: the exception alone says a method is missing
    /// and not which contract it belonged to.
    /// </para>
    /// </summary>
    public static PluginRefusal RemovedContractMember(string plugin, string missingMember)
    {
        return new PluginRefusal(
            PluginRefusalCodes.HostServicesRemoved,
            plugin,
            $"The plugin called a member contract v3 removed: {missingMember}",
            "Contract v3 hands a plugin facades on IPluginContext instead of the host's own container and event bus, so the owner can see and revoke every route into the server.",
            "Rebuild against NoMercy.Plugins.Abstractions 11.0 and replace the call with the facade for what it needed. Docs: /nomercy-plugins/migration/v2-to-v3",
            PluginRefusalSeverity.Blocked
        );
    }
}
