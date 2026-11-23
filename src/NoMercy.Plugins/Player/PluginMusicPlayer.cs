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

using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Media;
using NoMercy.Storage;

namespace NoMercy.PluginSdk.Player;

/// <summary>
/// One plugin's reach into the server's music player.
/// <para>
/// Every call asks the broker first: did the manifest declare the capability
/// and did the owner approve it. Reading what plays is
/// <see cref="PluginCapabilityNames.PlayerState" />; reading and arranging
/// the upcoming tracks, and offering audio for the join between two of them,
/// is <see cref="PluginCapabilityNames.PlayerQueue" />.
/// </para>
/// </summary>
/// <param name="broker">Resolved late: the broker reads the plugins this facade is built for.</param>
/// <param name="host">Resolved late: the music player is built after the plugins are.</param>
/// <param name="dataFolder">
/// The plugin's own folder, where the file of a transition lies. Null on a
/// host that wired no file access; a transition is then refused by name.
/// </param>
public sealed class PluginMusicPlayer(
    Ulid pluginId,
    Func<IPluginCapabilityBroker?> broker,
    Func<IPluginMusicPlayerHost?> host,
    string? dataFolder = null,
    IStorage? storage = null
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

    public Task<PluginMusicTransitionAnswer> OfferTransitionAsync(
        Guid userId,
        PluginMusicTransition transition,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentOutOfRangeException.ThrowIfNegative(transition.OutgoingStopMs);
        ArgumentOutOfRangeException.ThrowIfNegative(transition.IncomingResumeMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(transition.DurationMs, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            transition.DurationMs,
            PluginMusicTransition.MaxDurationMs
        );
        RequireAJsonObject(transition.Data);

        IPluginMusicPlayerHost player = Host(PluginCapabilityNames.PlayerQueue);

        // The server serves the file to the listener, which is what a media
        // link does. A plugin that may not mint one may not have one minted.
        if (broker()!.Check(pluginId, PluginCapabilityNames.MediaProxy) is { } refusal)
            throw new PluginRefusedException(refusal);

        if (dataFolder is null || storage is null)
            throw new PluginRefusedException(
                PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), Facade)
            );

        // Looked for now, so the plugin reads why at the call. The folder's
        // guard runs again on every request for the file.
        if (!new PluginDataFolder(pluginId, dataFolder, storage).Holds(transition.File))
            throw new PluginRefusedException(
                new(
                    PluginRefusalCodes.CapabilityScopeRefused,
                    pluginId.ToString(),
                    "The server did not take the transition.",
                    $"There is no file at {transition.File} in the plugin's own folder.",
                    "Offer a file that exists, with its path relative to context.Storage.Private.",
                    PluginRefusalSeverity.Blocked
                )
            );

        return player.OfferTransitionAsync(pluginId, userId, transition, ct);
    }

    public Task WithdrawTransitionAsync(Guid userId, CancellationToken ct = default) =>
        Host(PluginCapabilityNames.PlayerQueue).WithdrawTransitionAsync(pluginId, userId, ct);

    /// <summary>
    /// The players are handed this text as part of a message, so anything
    /// that is not one JSON object would break that message for all of them.
    /// </summary>
    private static void RequireAJsonObject(string? data)
    {
        if (data is null)
            return;

        if (data.Length > PluginMusicTransition.MaxDataLength)
            throw new ArgumentException(
                $"The data of a transition holds at most {PluginMusicTransition.MaxDataLength} characters.",
                nameof(data)
            );

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(data);

            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                return;
        }
        catch (JsonException) { }

        throw new ArgumentException(
            "The data of a transition is one JSON object, or null.",
            nameof(data)
        );
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
