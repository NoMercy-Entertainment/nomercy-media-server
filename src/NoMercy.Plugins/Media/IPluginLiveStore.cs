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

namespace NoMercy.PluginSdk.Media;

/// <summary>
/// What a plugin published, kept by the host.
/// <para>
/// The host owns this rather than the plugin, because the channel list is what
/// a client reads and the upstream addresses inside it are what must never
/// reach one. A plugin publishes and stops being involved.
/// </para>
/// </summary>
public interface IPluginLiveStore
{
    void SaveChannels(Ulid pluginId, IReadOnlyList<PluginLiveChannel> channels);

    void SaveGroups(Ulid pluginId, IReadOnlyList<PluginChannelGroup> groups);

    void SaveGuide(Ulid pluginId, IReadOnlyList<PluginEpgProgram> guide);

    IReadOnlyList<PluginLiveChannel> Channels(Ulid pluginId);

    IReadOnlyList<PluginChannelGroup> Groups(Ulid pluginId);

    PluginLiveChannel? Channel(Ulid pluginId, string channelId);

    /// <summary>What is on one channel at one moment, which is what a guide is asked.</summary>
    IReadOnlyList<PluginEpgProgram> ProgramsAt(Ulid pluginId, string channelId, DateTimeOffset at);
}
