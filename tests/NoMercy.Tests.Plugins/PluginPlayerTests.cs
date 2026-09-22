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

using System.Text.Json.Nodes;
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Player;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Playback, without handing a plugin the player.
/// <para>
/// Every case here is about who is allowed: a plugin reaches the viewer's
/// speakers only through a grant the owner gave, and a plugin without one is
/// silent rather than told it played something. The intent itself is a message
/// on the plugin's own channel, so the client that owns a player — and already
/// decided whether that audio is casting — is the one that acts.
/// </para>
/// </summary>
public class PluginPlayerTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private static PluginPlaybackSource Station =>
        new()
        {
            Url = "https://ice1.somafm.com/groovesalad-128-mp3",
            Title = "Groove Salad",
            IsLive = true,
            PluginId = PluginId,
        };

    [Fact]
    public async Task PlayAsync_WithTheHostGranted_PushesTheIntentToTheSubscribers()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(PluginGrantKind.PlayerSource, "ice1.somafm.com")
        );

        await player.PlayAsync(Station);

        hub.Sent.Should().ContainSingle();
        hub.Sent[0].Type.Should().Be(PluginPlayer.PlayMessage);
        hub.Sent[0].Payload["url"].Should().Be("https://ice1.somafm.com/groovesalad-128-mp3");
        hub.Sent[0].Payload["isLive"].Should().Be(true);
    }

    [Fact]
    public async Task PlayAsync_WithoutAGrant_ReachesNobody()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(PluginId, hub, FakeGrants.Holding(null, null));

        await player.PlayAsync(Station);

        hub.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// A grant names a host. One for a different host is not a grant for this
    /// one, or a plugin the owner allowed one stream from could play any.
    /// </summary>
    [Fact]
    public async Task PlayAsync_GrantForAnotherHost_DoesNotCarry()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(PluginGrantKind.PlayerSource, "example.com")
        );

        await player.PlayAsync(Station);

        hub.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PlayAsync_MalformedUrl_IsRefusedRatherThanWidened()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(PluginGrantKind.PlayerSource, "ice1.somafm.com")
        );

        await player.PlayAsync(
            new()
            {
                Url = "not a url",
                Title = "Nowhere",
                PluginId = PluginId,
            }
        );

        hub.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task EnqueueAsync_IsItsOwnMessage_SoAClientCanAddRatherThanReplace()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(PluginGrantKind.PlayerSource, "ice1.somafm.com")
        );

        await player.EnqueueAsync(Station);

        hub.Sent.Should().ContainSingle();
        hub.Sent[0].Type.Should().Be(PluginPlayer.EnqueueMessage);
    }

    [Fact]
    public async Task ControlAsync_KnownCommandWithTheCapabilityGranted_IsSent()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(
                PluginGrantKind.ForCapability(PluginCapability.Player),
                PluginGrant.Everything
            )
        );

        await player.ControlAsync(PluginPlaybackCommand.Pause);

        hub.Sent.Should().ContainSingle();
        hub.Sent[0].Type.Should().Be(PluginPlayer.ControlMessage);
        hub.Sent[0].Payload["command"].Should().Be("pause");
    }

    [Fact]
    public async Task ControlAsync_WordNobodyOffers_IsDroppedRatherThanForwarded()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(
                PluginGrantKind.ForCapability(PluginCapability.Player),
                PluginGrant.Everything
            )
        );

        await player.ControlAsync("selfDestruct");

        hub.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ControlAsync_WithTheSourceGrantOnly_IsNotEnough()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(PluginGrantKind.PlayerSource, PluginGrant.Everything)
        );

        await player.ControlAsync(PluginPlaybackCommand.Next);

        hub.Sent.Should().BeEmpty();
    }

    /// <summary>
    /// The server never sees playback state, so it says so rather than guessing.
    /// </summary>
    [Fact]
    public async Task GetStateAsync_IsNullBecauseTheServerOwnsNoPlayer()
    {
        RecordingHub hub = new();
        PluginPlayer player = new(
            PluginId,
            hub,
            FakeGrants.Holding(
                PluginGrantKind.ForCapability(PluginCapability.Player),
                PluginGrant.Everything
            )
        );

        (await player.GetStateAsync()).Should().BeNull();
    }

    private sealed record SentMessage(string Type, IReadOnlyDictionary<string, object?> Payload);

    private sealed class RecordingHub : IPluginHubContext
    {
        public List<SentMessage> Sent { get; } = [];

        public Task PushAsync(string type, object? payload)
        {
            Sent.Add(new(type, (IReadOnlyDictionary<string, object?>)payload!));
            return Task.CompletedTask;
        }

        public Task PushToUserAsync(string userId, string type, object? payload) =>
            PushAsync(type, payload);

        public List<string> Registered { get; } = [];

        public void Handle(
            string method,
            Func<PluginCaller, JsonNode?, CancellationToken, Task<object?>> handler
        ) => Registered.Add(method);
    }

    private sealed class FakeGrants(string? kind, string? value) : IPluginGrants
    {
        public static FakeGrants Holding(string? kind, string? value) => new(kind, value);

        public Task<bool> HasAsync(string asked, string askedValue, CancellationToken ct = default)
        {
            if (kind is null || value is null)
                return Task.FromResult(false);

            bool sameKind = string.Equals(asked, kind, StringComparison.OrdinalIgnoreCase);
            bool sameValue =
                value == PluginGrant.Everything
                || string.Equals(askedValue, value, StringComparison.OrdinalIgnoreCase);

            return Task.FromResult(sameKind && sameValue);
        }

        public Task<IReadOnlyList<string>> GetAsync(string asked, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                string.Equals(asked, kind, StringComparison.OrdinalIgnoreCase) && value is not null
                    ? [value]
                    : []
            );

        public Task RequestAsync(
            string asked,
            string askedValue,
            string reason,
            CancellationToken ct = default
        ) => Task.CompletedTask;
    }
}
