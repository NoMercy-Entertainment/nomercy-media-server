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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Media;

/// <summary>
/// A plugin publishes channels and a guide; the host keeps both.
/// <para>
/// The plugin knows the provider and stops being involved after publishing.
/// What a client is handed is a channel with a ticket on this server, never
/// the provider's address, because those addresses carry credentials.
/// </para>
/// </summary>
public class PluginMediaLive(Ulid pluginId, IPluginCapabilityBroker broker, IPluginLiveStore store)
    : IPluginMediaLive
{
    public Task PublishAsync(
        IReadOnlyList<PluginLiveChannel> channels,
        CancellationToken ct = default
    )
    {
        Allowed();

        // Every host a channel names, not just the first, and before anything
        // is stored: a channel list holding an address the manifest never
        // named is a list the server would serve from.
        foreach (PluginLiveChannel channel in channels)
        {
            foreach (PluginProxyLink link in channel.Links)
            {
                if (
                    broker.Check(pluginId, PluginCapabilityNames.MediaProxy, link.Url.Host) is
                    { } refusal
                )
                    throw new PluginRefusedException(refusal);
            }
        }

        store.SaveChannels(pluginId, channels);

        return Task.CompletedTask;
    }

    public Task PublishGuideAsync(
        IReadOnlyList<PluginEpgProgram> guide,
        CancellationToken ct = default
    )
    {
        Allowed();
        store.SaveGuide(pluginId, guide);

        return Task.CompletedTask;
    }

    public Task PublishGroupsAsync(
        IReadOnlyList<PluginChannelGroup> groups,
        CancellationToken ct = default
    )
    {
        Allowed();
        store.SaveGroups(pluginId, groups);

        return Task.CompletedTask;
    }

    private void Allowed()
    {
        if (broker.Check(pluginId, PluginCapabilityNames.MediaLive) is { } refusal)
            throw new PluginRefusedException(refusal);
    }
}
