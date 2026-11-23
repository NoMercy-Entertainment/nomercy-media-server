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
/// The payload of <see cref="PluginTopics.MusicPlaybackPaused" />,
/// <see cref="PluginTopics.MusicPlaybackResumed" /> and
/// <see cref="PluginTopics.MusicPlaybackSeeked" />: where a listener's track
/// is at that moment. Added in ABI 12.14.
/// </summary>
/// <param name="UserId">The listener.</param>
/// <param name="TrackId">The track that plays.</param>
/// <param name="PositionMs">Where the track is at this moment.</param>
public sealed record PluginMusicPlaybackPosition(Guid UserId, Guid TrackId, int PositionMs);
