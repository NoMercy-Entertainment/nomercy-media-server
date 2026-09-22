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
/// What a plugin asks a player to do, sent to the clients subscribed to it.
/// <para>
/// The server has no player. The player lives in whichever client the viewer is
/// looking at, so every method here is a message on the plugin's own channel and
/// the client that owns a player acts on it. That is what makes casting work
/// without a plugin knowing casting exists: the client already decided where its
/// audio goes, and an intent arriving there goes with it.
/// </para>
/// <para>
/// Gated by grants, not by the manifest. Declaring a capability is an intention;
/// the grant is the owner's permission. A plugin without one reaches nobody
/// rather than being told it played something it did not.
/// </para>
/// </summary>
public class PluginPlayer(Ulid pluginId, IPluginHubContext hub, IPluginGrants grants)
    : IPluginPlayer
{
    /// <summary>The message types a client listens for on the plugin channel.</summary>
    public const string PlayMessage = "player.play";
    public const string EnqueueMessage = "player.enqueue";
    public const string ControlMessage = "player.control";

    public Task PlayAsync(PluginPlaybackSource source, CancellationToken ct = default) =>
        SendSource(PlayMessage, source, ct);

    public Task EnqueueAsync(PluginPlaybackSource source, CancellationToken ct = default) =>
        SendSource(EnqueueMessage, source, ct);

    /// <summary>
    /// A command the viewer already has. An unknown word is dropped here rather
    /// than sent on: every client would have to reject it separately, and one
    /// that guessed would be inventing a control nobody offered.
    /// </summary>
    public async Task ControlAsync(string command, CancellationToken ct = default)
    {
        if (!PluginPlaybackCommand.IsKnown(command))
            return;

        if (
            !await grants.HasAsync(
                PluginGrantKind.ForCapability(PluginCapability.Player),
                PluginGrant.Everything,
                ct
            )
        )
            return;

        await hub.PushAsync(
            ControlMessage,
            new Dictionary<string, object?> { ["command"] = command }
        );
    }

    /// <summary>
    /// What is playing, as far as the server knows — which is nothing.
    /// <para>
    /// State lives in the client that owns the player and is not reported back,
    /// so this answers null rather than a stale guess. A plugin that needs to
    /// know asks its own subscribers over its channel and hears it there.
    /// </para>
    /// </summary>
    public Task<PluginPlaybackState?> GetStateAsync(CancellationToken ct = default) =>
        Task.FromResult<PluginPlaybackState?>(null);

    /// <summary>
    /// Playing a URL the library does not own is its own grant, per host.
    /// <para>
    /// Finer than the player capability on purpose: arranging a viewer's own
    /// media and deciding what comes out of their speakers are different amounts
    /// of trust, and a grant naming the host is one the owner can read.
    /// </para>
    /// </summary>
    private async Task SendSource(string type, PluginPlaybackSource source, CancellationToken ct)
    {
        if (!await grants.HasAsync(PluginGrantKind.PlayerSource, HostOf(source.Url), ct))
            return;

        await hub.PushAsync(
            type,
            new Dictionary<string, object?>
            {
                ["url"] = source.Url,
                ["title"] = source.Title,
                ["artist"] = source.Artist,
                ["artworkUrl"] = source.ArtworkUrl,
                ["isLive"] = source.IsLive,
                ["pluginId"] = pluginId.ToString(),
            }
        );
    }

    /// <summary>
    /// The host a grant is read against. A URL that will not parse yields the
    /// whole string, which matches no grant — a malformed source is refused
    /// rather than quietly widened to everything.
    /// </summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? parsed.Host : url;
}
