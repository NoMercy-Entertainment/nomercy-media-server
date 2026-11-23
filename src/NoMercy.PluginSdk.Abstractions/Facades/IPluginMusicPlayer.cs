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
/// The server's own music player, for one named listener.
/// <para>
/// The rest of <see cref="IPluginPlayer" /> sends an intent to whichever
/// client is looking. Music is different: the server keeps the state of every
/// listener's music itself (the track, the position, the upcoming tracks), so
/// a plugin can read it and arrange it for a listener who never opened the
/// plugin's page.
/// </para>
/// <para>
/// A plugin arranges what the listener queued. It does not add a track the
/// listener did not queue and does not remove one. Added in ABI 12.14.
/// </para>
/// </summary>
public interface IPluginMusicPlayer
{
    /// <summary>
    /// What plays for a listener now, or null when nothing does. Needs
    /// <see cref="PluginCapabilityNames.PlayerState" />.
    /// </summary>
    Task<PluginMusicPlayback?> GetPlaybackAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// The ids of the listener's upcoming tracks, the next one first. Empty
    /// when nothing plays. Needs <see cref="PluginCapabilityNames.PlayerQueue" />.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetQueueAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Moves a track that is in the upcoming tracks to another place among
    /// them; 0 is the front. A place past the end is the end. A track that is
    /// queued more than once moves by its first place. Needs
    /// <see cref="PluginCapabilityNames.PlayerQueue" />.
    /// </summary>
    Task<PluginMusicQueueAnswer> MoveInQueueAsync(
        Guid userId,
        Guid trackId,
        int position,
        CancellationToken ct = default
    );

    /// <summary>
    /// Puts all the upcoming tracks in a new order, in one step: the
    /// listener's players are told once and
    /// <see cref="PluginTopics.MusicPlaybackQueueChanged" /> is said once.
    /// <paramref name="order" /> names the tracks that are queued, each as
    /// often as it is queued; anything else is answered with
    /// <see cref="PluginMusicQueueAnswer.NotInQueue" /> and nothing changes.
    /// Needs <see cref="PluginCapabilityNames.PlayerQueue" />.
    /// </summary>
    Task<PluginMusicQueueAnswer> ReorderQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> order,
        CancellationToken ct = default
    );
}
