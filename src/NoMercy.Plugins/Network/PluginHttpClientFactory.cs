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

namespace NoMercy.PluginSdk.Network;

public static class PluginHttpClientFactory
{
    /// <param name="grantedHosts">
    /// Read on every request rather than captured once, so a host the owner
    /// grants after the plugin has started takes effect without a restart.
    /// </param>
    public static HttpClient Create(
        PluginCapabilities? capabilities,
        Func<IReadOnlyList<string>>? grantedHosts = null,
        Ulid pluginId = default,
        string? pluginName = null,
        Version? pluginVersion = null
    )
    {
        IReadOnlyList<string> hosts = capabilities?.Network?.Hosts ?? [];

        // Allowlist outermost so a denied host never reaches the socket, with
        // the user agent stamped on the way past.
        PluginUserAgentHandler userAgent = new(pluginId, pluginName, pluginVersion)
        {
            InnerHandler = new SocketsHttpHandler(),
        };

        PluginNetworkAllowlistHandler handler = new(hosts, grantedHosts)
        {
            InnerHandler = userAgent,
        };

        return new(handler);
    }
}
