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
    /// Published after every finished track analysis, Ok or Failed. The
    /// payload deserialises as <see cref="PluginMusicAnalysisCompleted" />.
    /// </summary>
    public const string MusicAnalysisCompleted = "music.analysis.completed";

    /// <summary>
    /// Published when a track's lyrics were asked for and the server had
    /// none, or only text without timing: the cue for a plugin that can
    /// generate timing. At most once per track per day. The payload
    /// deserialises as <see cref="PluginLyricsRequested" />. Added in ABI 12.6.
    /// </summary>
    public const string LyricsRequested = "music.lyrics.requested";
}
