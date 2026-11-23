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

using FluentAssertions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a person listens to reaches only a plugin that declares
/// <c>player.state</c> and whose owner approved it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginPlaybackTopicGateTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    private static readonly Guid Listener = Guid.NewGuid();

    private readonly InMemoryEventBus _bus = new();
    private readonly List<PluginMusicPlaybackStopped> _heard = [];

    public static TheoryData<string> PlaybackTopics() =>
        [
            PluginTopics.MusicPlaybackStarted,
            PluginTopics.MusicPlaybackPaused,
            PluginTopics.MusicPlaybackResumed,
            PluginTopics.MusicPlaybackSeeked,
            PluginTopics.MusicPlaybackStopped,
            PluginTopics.MusicPlaybackQueueChanged,
            PluginTopics.MusicPlaybackDeviceChanged,
            PluginTopics.MusicPlaybackModeChanged,
        ];

    [Theory]
    [MemberData(nameof(PlaybackTopics))]
    public async Task A_plugin_that_holds_the_capability_hears_the_topic(string topic)
    {
        using PluginEvents events = Subscribed(topic, new RecordingBroker(refusal: null));

        await HostSays(topic);

        _heard.Should().ContainSingle().Which.UserId.Should().Be(Listener);
    }

    [Theory]
    [MemberData(nameof(PlaybackTopics))]
    public async Task A_plugin_without_the_capability_hears_none_of_them(string topic)
    {
        using PluginEvents events = Subscribed(topic, new RecordingBroker(Refused()));

        await HostSays(topic);

        _heard.Should().BeEmpty();
    }

    /// <summary>A host that wired no broker cannot have asked the owner, so nobody hears.</summary>
    [Theory]
    [MemberData(nameof(PlaybackTopics))]
    public async Task Without_a_broker_nobody_hears_the_topic(string topic)
    {
        using PluginEvents events = Subscribed(topic, broker: null);

        await HostSays(topic);

        _heard.Should().BeEmpty();
    }

    [Fact]
    public async Task The_capability_asked_for_is_player_state_for_the_subscribing_plugin()
    {
        RecordingBroker broker = new(refusal: null);
        using PluginEvents events = Subscribed(PluginTopics.MusicPlaybackStopped, broker);

        await HostSays(PluginTopics.MusicPlaybackStopped);

        broker.Asked.Should().Equal((PluginId, PluginCapabilityNames.PlayerState));
    }

    /// <summary>
    /// Asked on every delivery, so an approval given while the plugin runs
    /// counts from the next thing that happens.
    /// </summary>
    [Fact]
    public async Task An_approval_given_later_counts_without_subscribing_again()
    {
        RecordingBroker broker = new(Refused());
        using PluginEvents events = Subscribed(PluginTopics.MusicPlaybackStopped, broker);

        await HostSays(PluginTopics.MusicPlaybackStopped);
        broker.Refusal = null;
        await HostSays(PluginTopics.MusicPlaybackStopped);

        _heard.Should().ContainSingle();
    }

    [Fact]
    public async Task A_topic_that_needs_no_capability_never_asks_the_broker()
    {
        RecordingBroker broker = new(Refused());
        List<PluginLyricsRequested> requested = [];
        using PluginEvents events = new(PluginId, _bus, null, () => broker);
        events.Subscribe<PluginLyricsRequested>(
            PluginTopics.LyricsRequested,
            (payload, _) =>
            {
                requested.Add(payload);
                return Task.CompletedTask;
            }
        );

        await _bus.PublishAsync(
            PluginMessageEvent.From(
                Ulid.Empty,
                PluginTopics.LyricsRequested,
                new PluginLyricsRequested(Guid.NewGuid(), HasSyncedTiming: false)
            )
        );

        requested.Should().ContainSingle();
        broker.Asked.Should().BeEmpty();
    }

    private Task HostSays(string topic) =>
        _bus.PublishAsync(
            PluginMessageEvent.From(Ulid.Empty, topic, new PluginMusicPlaybackStopped(Listener))
        );

    private PluginEvents Subscribed(string topic, IPluginCapabilityBroker? broker)
    {
        PluginEvents events = new(PluginId, _bus, null, broker is null ? null : () => broker);
        events.Subscribe<PluginMusicPlaybackStopped>(
            topic,
            (payload, _) =>
            {
                _heard.Add(payload);
                return Task.CompletedTask;
            }
        );
        return events;
    }

    private static PluginRefusal Refused() =>
        new(
            PluginRefusalCodes.CapabilityNotDeclared,
            "scrobbler",
            "The plugin listened for what a person plays.",
            "It did not declare the player.state capability.",
            "Declare it in the manifest.",
            PluginRefusalSeverity.Blocked
        );

    private sealed class RecordingBroker(PluginRefusal? refusal) : IPluginCapabilityBroker
    {
        public PluginRefusal? Refusal { get; set; } = refusal;

        public List<(Ulid PluginId, string Capability)> Asked { get; } = [];

        public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null)
        {
            Asked.Add((pluginId, capability));
            return Refusal;
        }
    }
}
