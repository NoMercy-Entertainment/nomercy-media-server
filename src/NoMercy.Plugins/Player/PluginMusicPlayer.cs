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
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Player;

/// <summary>
/// One plugin's reach into the server's music player.
/// <para>
/// Every call asks the broker first: did the manifest declare the capability
/// and did the owner approve it. Reading what plays is
/// <see cref="PluginCapabilityNames.PlayerState" />; reading and arranging
/// the upcoming tracks is <see cref="PluginCapabilityNames.PlayerQueue" />.
/// </para>
/// </summary>
/// <param name="broker">Resolved late: the broker reads the plugins this facade is built for.</param>
/// <param name="host">Resolved late: the music player is built after the plugins are.</param>
public sealed class PluginMusicPlayer(
    Ulid pluginId,
    Func<IPluginCapabilityBroker?> broker,
    Func<IPluginMusicPlayerHost?> host
) : IPluginMusicPlayer
{
    private const string Facade = "IPluginContext.Player.Music";

    public Task<PluginMusicPlayback?> GetPlaybackAsync(
        Guid userId,
        CancellationToken ct = default
    ) => Task.FromResult(Host(PluginCapabilityNames.PlayerState).Playback(userId));

    public Task<IReadOnlyList<Guid>> GetQueueAsync(Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Host(PluginCapabilityNames.PlayerQueue).Playback(userId)?.Queue ?? []);

    public Task<PluginMusicQueueAnswer> MoveInQueueAsync(
        Guid userId,
        Guid trackId,
        int position,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        return Host(PluginCapabilityNames.PlayerQueue)
            .MoveInQueueAsync(userId, trackId, position, ct);
    }

    public Task<PluginMusicQueueAnswer> ReorderQueueAsync(
        Guid userId,
        IReadOnlyList<Guid> order,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(order);

        return Host(PluginCapabilityNames.PlayerQueue).ReorderQueueAsync(userId, order, ct);
    }

    /// <summary>
    /// The music player, once the plugin is known to hold
    /// <paramref name="capability" />. A host that wired no broker or no music
    /// player refuses by name, so nothing is read that nothing checked.
    /// </summary>
    private IPluginMusicPlayerHost Host(string capability)
    {
        if (broker() is not { } check || host() is not { } player)
            throw new PluginRefusedException(
                PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), Facade)
            );

        if (check.Check(pluginId, capability) is { } refusal)
            throw new PluginRefusedException(refusal);

        return player;
    }
}
