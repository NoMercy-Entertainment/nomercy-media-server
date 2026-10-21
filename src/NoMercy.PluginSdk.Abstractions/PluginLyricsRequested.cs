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
/// The payload of <see cref="PluginTopics.LyricsRequested" />: a track was
/// played and its lyrics are missing or carry no timing. Added in ABI 12.6.
/// </summary>
/// <param name="TrackId">The track whose lyrics were asked for.</param>
/// <param name="HasSyncedTiming">False when the server has no lyrics at all; true when it has text whose lines are timed, which never raises this topic, so it is false in practice and present for the shape to stay stable.</param>
public sealed record PluginLyricsRequested(Guid TrackId, bool HasSyncedTiming);
