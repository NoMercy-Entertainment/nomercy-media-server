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
/// What plays for one listener at the moment it was asked. Added in ABI 12.14.
/// </summary>
/// <param name="UserId">The listener.</param>
/// <param name="TrackId">The track that plays.</param>
/// <param name="DeviceId">The device that makes the sound; null when no device holds the session.</param>
/// <param name="IsPlaying">False while the track is paused.</param>
/// <param name="PositionMs">Where the track is.</param>
/// <param name="Queue">The ids of the upcoming tracks, the next one first.</param>
/// <param name="Repeat"><c>"off"</c>, <c>"one"</c> or <c>"all"</c>.</param>
/// <param name="Shuffle">The listener's shuffle switch.</param>
public sealed record PluginMusicPlayback(
    Guid UserId,
    Guid TrackId,
    string? DeviceId,
    bool IsPlaying,
    int PositionMs,
    IReadOnlyList<Guid> Queue,
    string Repeat,
    bool Shuffle
)
{
    /// <summary>
    /// The list the listener plays from, as the app's own route to it, for
    /// example <c>/music/album/{id}</c>. Null when the server does not say.
    /// </summary>
    public string? CurrentList { get; init; }
}
