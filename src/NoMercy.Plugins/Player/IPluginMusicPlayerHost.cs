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

namespace NoMercy.PluginSdk.Player;

/// <summary>
/// The server's music player as the plugin facade reaches it. The server
/// implements it; a host without a music player registers none, and
/// <see cref="IPluginPlayer.Music" /> then refuses by name.
/// <para>
/// Whether the plugin may ask is decided before a call arrives here. What is
/// answered here is only what the player's state allows.
/// </para>
/// </summary>
public interface IPluginMusicPlayerHost
{
    /// <summary>What plays for a listener now, or null when nothing does.</summary>
    PluginMusicPlayback? Playback(Guid userId);

    Task<PluginMusicQueueAnswer> MoveInQueueAsync(
        Guid userId,
        Guid trackId,
        int position,
        CancellationToken ct = default
    );

    Task<PluginMusicQueueAnswer> ReorderQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> order,
        CancellationToken ct = default
    );
}
