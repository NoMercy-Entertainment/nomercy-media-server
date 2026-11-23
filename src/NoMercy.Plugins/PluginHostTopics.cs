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

using System.Collections.Frozen;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// The topics only the host may publish. A subscriber takes them as the
/// server's word, so a plugin may listen to them and never send them.
/// </summary>
internal static class PluginHostTopics
{
    /// <summary>
    /// What a listener plays. Personal, so each is delivered only to a plugin
    /// that holds <see cref="PluginCapabilityNames.PlayerState" />.
    /// </summary>
    private static readonly FrozenSet<string> MusicPlayback = FrozenSet.ToFrozenSet(
        [
            PluginTopics.MusicPlaybackStarted,
            PluginTopics.MusicPlaybackPaused,
            PluginTopics.MusicPlaybackResumed,
            PluginTopics.MusicPlaybackSeeked,
            PluginTopics.MusicPlaybackStopped,
            PluginTopics.MusicPlaybackQueueChanged,
            PluginTopics.MusicPlaybackDeviceChanged,
            PluginTopics.MusicPlaybackModeChanged,
        ],
        StringComparer.Ordinal
    );

    /// <summary>
    /// A topic the host published until ABI 13.0, when music analysis left
    /// the server. It stays reserved: a plugin built against 12 may still
    /// listen for it, and a plugin that could publish it now would be taken
    /// for the server.
    /// </summary>
    private const string RetiredMusicAnalysisCompleted = "music.analysis.completed";

    private static readonly FrozenSet<string> Reserved = FrozenSet.ToFrozenSet(
        [RetiredMusicAnalysisCompleted, PluginTopics.LyricsRequested, .. MusicPlayback],
        StringComparer.Ordinal
    );

    /// <summary>The sender id every host publish carries.</summary>
    public static readonly Ulid HostSender = Ulid.Empty;

    public static bool IsReserved(string topic) => Reserved.Contains(topic);

    /// <summary>
    /// The capability a plugin must hold to be handed this topic, or null for
    /// a topic every plugin may hear.
    /// </summary>
    public static string? CapabilityFor(string topic) =>
        MusicPlayback.Contains(topic) ? PluginCapabilityNames.PlayerState : null;
}
