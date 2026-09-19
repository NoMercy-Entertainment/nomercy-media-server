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
}
