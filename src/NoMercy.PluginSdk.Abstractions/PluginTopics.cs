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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Names of the topics the host itself publishes on <see cref="IPluginEvents" />,
/// as opposed to the ones a plugin defines for its own use.
/// </summary>
public static class PluginTopics
{
    /// <summary>
    /// Published when a track's lyrics were asked for and the server had
    /// none, or only text without timing: the cue for a plugin that can
    /// generate timing. At most once per track per day. The payload
    /// deserialises as <see cref="PluginLyricsRequested" />. Added in ABI 12.6.
    /// </summary>
    public const string LyricsRequested = "music.lyrics.requested";

    /// <summary>
    /// A track started for a listener, by any route: a start, next, previous,
    /// the end of the track before it, or a join a player completed. The
    /// payload deserialises as <see cref="PluginMusicPlaybackStarted" />.
    /// <para>
    /// This and the other <c>music.playback</c> topics say what a person
    /// listens to, so they reach only a plugin that declares
    /// <see cref="PluginCapabilityNames.PlayerState" /> and whose owner
    /// approved it. There is no tick for the position: with started, paused,
    /// resumed and seeked a plugin works it out. Added in ABI 12.14.
    /// </para>
    /// </summary>
    public const string MusicPlaybackStarted = "music.playback.started";

    /// <summary>
    /// The listener's track was paused. The payload deserialises as
    /// <see cref="PluginMusicPlaybackPosition" />. Added in ABI 12.14.
    /// </summary>
    public const string MusicPlaybackPaused = "music.playback.paused";

    /// <summary>
    /// The listener's track plays again after a pause. The payload
    /// deserialises as <see cref="PluginMusicPlaybackPosition" />. Added in
    /// ABI 12.14.
    /// </summary>
    public const string MusicPlaybackResumed = "music.playback.resumed";

    /// <summary>
    /// The position jumped by two seconds or more while the track stayed the
    /// same: the listener sought, or the player corrected the server's clock
    /// by that much. A smaller step is not announced. The payload
    /// deserialises as <see cref="PluginMusicPlaybackPosition" />. Added in
    /// ABI 12.14.
    /// </summary>
    public const string MusicPlaybackSeeked = "music.playback.seeked";

    /// <summary>
    /// Nothing plays for the listener any more. The payload deserialises as
    /// <see cref="PluginMusicPlaybackStopped" />. Added in ABI 12.14.
    /// </summary>
    public const string MusicPlaybackStopped = "music.playback.stopped";

    /// <summary>
    /// The upcoming tracks changed while a track plays. A track that starts
    /// carries its own queue, so this is not sent for the step from one track
    /// to the next. The payload deserialises as
    /// <see cref="PluginMusicPlaybackQueueChanged" />. Added in ABI 12.14.
    /// </summary>
    public const string MusicPlaybackQueueChanged = "music.playback.queue.changed";

    /// <summary>
    /// Another device makes the sound for the listener. The payload
    /// deserialises as <see cref="PluginMusicPlaybackDeviceChanged" />. Added
    /// in ABI 12.14.
    /// </summary>
    public const string MusicPlaybackDeviceChanged = "music.playback.device.changed";

    /// <summary>
    /// The listener changed repeat or shuffle while a track plays. The payload
    /// deserialises as <see cref="PluginMusicPlaybackModeChanged" />. Added in
    /// ABI 12.14.
    /// </summary>
    public const string MusicPlaybackModeChanged = "music.playback.mode.changed";
}
