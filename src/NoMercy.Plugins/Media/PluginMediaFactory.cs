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

using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Network;

namespace NoMercy.Plugins.Media;

/// <summary>
/// Builds one plugin's media facade.
/// <para>
/// The client it hands the proxy has no timeout, because a live stream never
/// finishes and a fixed one would cut it mid-song. It runs through the same
/// allowlist handler every other plugin request does, so the broker check in
/// the proxy is the second lock on that door rather than the only one.
/// </para>
/// </summary>
public class PluginMediaFactory(
    IPluginManifestSource manifests,
    IPluginCapabilityBroker broker,
    IPluginGrantStore grants,
    PluginMediaTicketMinter minter,
    IPluginCallerAccessor caller,
    ILoggerFactory loggers
) : IPluginMediaFactory
{
    public IPluginMedia CreateFor(Ulid pluginId) => new PluginMedia(pluginId, Build(pluginId));

    public IPluginMediaFetcher FetcherFor(Ulid pluginId) => Build(pluginId);

    private PluginMediaProxy Build(Ulid pluginId)
    {
        PluginInfo? info = manifests.Find(pluginId);

        HttpClient http = PluginHttpClientFactory.Create(
            info?.Capabilities,
            () => grants.Granted(pluginId, PluginGrantKind.NetworkHost),
            pluginId,
            info?.Name,
            info?.Version
        );

        http.Timeout = Timeout.InfiniteTimeSpan;

        return new(
            pluginId,
            caller,
            broker,
            minter,
            http,
            loggers.CreateLogger($"NoMercy.Plugins.Media.{pluginId}")
        );
    }
}
