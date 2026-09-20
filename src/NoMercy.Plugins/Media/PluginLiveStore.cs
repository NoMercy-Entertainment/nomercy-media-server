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

using System.Collections.Concurrent;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Media;

/// <summary>
/// The channels, groups and guide one plugin published, held in memory.
/// <para>
/// In memory on purpose. A channel carries a callback that resolves a
/// credential-bearing address, and a callback cannot be written to disk. A
/// plugin republishes on start, which is what it already does to pick up a
/// provider's changes.
/// </para>
/// </summary>
public class PluginLiveStore : IPluginLiveStore
{
    private readonly ConcurrentDictionary<Ulid, IReadOnlyList<PluginLiveChannel>> _channels = new();
    private readonly ConcurrentDictionary<Ulid, IReadOnlyList<PluginChannelGroup>> _groups = new();
    private readonly ConcurrentDictionary<Ulid, IReadOnlyList<PluginEpgProgram>> _guide = new();

    public void SaveChannels(Ulid pluginId, IReadOnlyList<PluginLiveChannel> channels) =>
        _channels[pluginId] = channels;

    public void SaveGroups(Ulid pluginId, IReadOnlyList<PluginChannelGroup> groups) =>
        _groups[pluginId] = groups;

    public void SaveGuide(Ulid pluginId, IReadOnlyList<PluginEpgProgram> guide) =>
        _guide[pluginId] = guide;

    public IReadOnlyList<PluginLiveChannel> Channels(Ulid pluginId) =>
        _channels.TryGetValue(pluginId, out IReadOnlyList<PluginLiveChannel>? channels)
            ? channels
            : [];

    public IReadOnlyList<PluginChannelGroup> Groups(Ulid pluginId) =>
        _groups.TryGetValue(pluginId, out IReadOnlyList<PluginChannelGroup>? groups) ? groups : [];

    public PluginLiveChannel? Channel(Ulid pluginId, string channelId) =>
        Channels(pluginId)
            .FirstOrDefault(channel =>
                channel.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase)
            );

    public IReadOnlyList<PluginEpgProgram> ProgramsAt(
        Ulid pluginId,
        string channelId,
        DateTimeOffset at
    ) =>
        _guide.TryGetValue(pluginId, out IReadOnlyList<PluginEpgProgram>? guide)
            ?
            [
                .. guide.Where(program =>
                    program.ChannelId.Equals(channelId, StringComparison.OrdinalIgnoreCase)
                    && program.Start <= at
                    // A program that stops exactly now is the one that just
                    // ended, not the one on.
                    && program.Stop > at
                ),
            ]
            : [];
}
