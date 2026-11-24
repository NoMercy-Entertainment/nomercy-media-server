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
/// The payload of <see cref="PluginTopics.MusicPlaybackModeChanged" />: the
/// listener changed repeat or shuffle while a track plays, which changes
/// what comes next without changing the queue. Added in ABI 12.14.
/// </summary>
/// <param name="UserId">The listener.</param>
/// <param name="TrackId">The track that plays.</param>
/// <param name="Repeat"><c>"off"</c>, <c>"one"</c> or <c>"all"</c>.</param>
/// <param name="Shuffle">The listener's shuffle switch.</param>
public sealed record PluginMusicPlaybackModeChanged(
    Guid UserId,
    Guid TrackId,
    string Repeat,
    bool Shuffle
);
