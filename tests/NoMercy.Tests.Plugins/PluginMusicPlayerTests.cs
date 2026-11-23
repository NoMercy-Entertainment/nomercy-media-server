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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Hub;
using NoMercy.PluginSdk.Player;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin's reach into the server's music player: nothing is read or
/// arranged for a plugin the owner did not let do so.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginMusicPlayerTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    private static readonly Guid Listener = Guid.NewGuid();
    private static readonly Guid Track = Guid.NewGuid();

    private readonly RecordingHost _host = new();
    private readonly RecordingBroker _broker = new();

    [Fact]
    public async Task Reading_what_plays_asks_for_player_state()
    {
        PluginMusicPlayback? playback = await Player().GetPlaybackAsync(Listener);

        playback.Should().BeSameAs(_host.Now);
        _broker.Asked.Should().Equal((PluginId, PluginCapabilityNames.PlayerState));
    }

    [Fact]
    public async Task Reading_the_queue_asks_for_player_queue()
    {
        IReadOnlyList<Guid> queue = await Player().GetQueueAsync(Listener);

        queue.Should().Equal(_host.Now.Queue);
        _broker.Asked.Should().Equal((PluginId, PluginCapabilityNames.PlayerQueue));
    }

    [Fact]
    public async Task The_queue_of_a_listener_who_plays_nothing_is_empty()
    {
        (await Player().GetQueueAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task A_move_asks_for_player_queue_and_reaches_the_player()
    {
        PluginMusicQueueAnswer answer = await Player().MoveInQueueAsync(Listener, Track, 2);

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        _host.Moves.Should().Equal((Listener, Track, 2));
        _broker.Asked.Should().Equal((PluginId, PluginCapabilityNames.PlayerQueue));
    }

    [Fact]
    public async Task A_new_order_asks_for_player_queue_and_reaches_the_player()
    {
        Guid[] order = [Guid.NewGuid(), Guid.NewGuid()];

        PluginMusicQueueAnswer answer = await Player().ReorderQueueAsync(Listener, order);

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        _host.Orders.Should().ContainSingle().Which.Should().Equal(order);
        _broker.Asked.Should().Equal((PluginId, PluginCapabilityNames.PlayerQueue));
    }

    /// <summary>
    /// Refused before the player is asked anything: a read that ran and was
    /// then refused has already told the plugin's code path what plays.
    /// </summary>
    [Fact]
    public async Task A_plugin_without_the_capability_is_refused_and_the_player_is_not_asked()
    {
        _broker.Refusal = Refused();
        PluginMusicPlayer player = Player();

        Func<Task>[] calls =
        [
            () => player.GetPlaybackAsync(Listener),
            () => player.GetQueueAsync(Listener),
            () => player.MoveInQueueAsync(Listener, Track, 0),
            () => player.ReorderQueueAsync(Listener, [Track]),
        ];

        foreach (Func<Task> call in calls)
            (await call.Should().ThrowAsync<PluginRefusedException>())
                .Which.Refusal.Should()
                .BeSameAs(_broker.Refusal);

        _host.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_host_without_a_music_player_refuses_by_name()
    {
        PluginMusicPlayer player = new(PluginId, () => _broker, () => null);

        Func<Task> call = () => player.GetPlaybackAsync(Listener);

        (await call.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.What.Should()
            .Contain("IPluginContext.Player.Music");
        _broker.Asked.Should().BeEmpty();
    }

    /// <summary>With nothing to ask the owner's answer of, nothing is read.</summary>
    [Fact]
    public async Task A_host_without_a_broker_refuses_and_the_player_is_not_asked()
    {
        PluginMusicPlayer player = new(PluginId, () => null, () => _host);

        Func<Task> call = () => player.MoveInQueueAsync(Listener, Track, 0);

        await call.Should().ThrowAsync<PluginRefusedException>();
        _host.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_place_before_the_front_is_a_mistake_in_the_call()
    {
        Func<Task> call = () => Player().MoveInQueueAsync(Listener, Track, -1);

        await call.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _host.Calls.Should().Be(0);
    }

    [Fact]
    public void A_player_built_without_a_music_player_refuses_by_name()
    {
        PluginPlayer player = new(PluginId, new NullPluginHubContext(), new NoGrants());

        Action read = () => _ = player.Music;

        read.Should()
            .Throw<PluginRefusedException>()
            .Which.Refusal.What.Should()
            .Contain("IPluginContext.Player.Music");
    }

    /// <summary>
    /// The context a plugin is handed carries the facade, and it is the
    /// broker's answer that decides, not how the host happened to be wired.
    /// </summary>
    [Fact]
    public async Task The_context_a_plugin_gets_reaches_the_music_player_through_the_broker()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nomercy-music-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            PluginContext context = TestPluginPlatform.Context(
                new InMemoryEventBus(),
                folder,
                TestStorageHelper.CreateStorage(folder),
                PluginId,
                new Services(_broker, _host)
            );

            (await context.Player.Music.GetPlaybackAsync(Listener)).Should().BeSameAs(_host.Now);

            _broker.Refusal = Refused();
            Func<Task> refused = () => context.Player.Music.GetPlaybackAsync(Listener);
            await refused.Should().ThrowAsync<PluginRefusedException>();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private PluginMusicPlayer Player() => new(PluginId, () => _broker, () => _host);

    private static PluginRefusal Refused() =>
        new(
            PluginRefusalCodes.CapabilityNotDeclared,
            "arranger",
            "The plugin arranged a listener's queue.",
            "It did not declare the player.queue capability.",
            "Declare it in the manifest.",
            PluginRefusalSeverity.Blocked
        );

    private sealed class Services(IPluginCapabilityBroker broker, IPluginMusicPlayerHost host)
        : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginCapabilityBroker) ? broker
            : serviceType == typeof(IPluginMusicPlayerHost) ? host
            : null;
    }

    private sealed class NoGrants : IPluginGrants
    {
        public Task<bool> HasAsync(string kind, string value, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task RequestAsync(
            string kind,
            string value,
            string reason,
            CancellationToken ct = default
        ) => Task.CompletedTask;
    }

    private sealed class RecordingBroker : IPluginCapabilityBroker
    {
        public PluginRefusal? Refusal { get; set; }

        public List<(Ulid PluginId, string Capability)> Asked { get; } = [];

        public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null)
        {
            Asked.Add((pluginId, capability));
            return Refusal;
        }
    }

    private sealed class RecordingHost : IPluginMusicPlayerHost
    {
        public PluginMusicPlayback Now { get; } =
            new(Listener, Track, "phone", true, 1000, [Guid.NewGuid()], "off", false);

        public int Calls { get; private set; }

        public List<(Guid UserId, Guid TrackId, int Position)> Moves { get; } = [];

        public List<IReadOnlyList<Guid>> Orders { get; } = [];

        public PluginMusicPlayback? Playback(Guid userId)
        {
            Calls++;
            return userId == Listener ? Now : null;
        }

        public Task<PluginMusicQueueAnswer> MoveInQueueAsync(
            Guid userId,
            Guid trackId,
            int position,
            CancellationToken ct = default
        )
        {
            Calls++;
            Moves.Add((userId, trackId, position));
            return Task.FromResult(PluginMusicQueueAnswer.Accepted);
        }

        public Task<PluginMusicQueueAnswer> ReorderQueueAsync(
            Guid userId,
            IReadOnlyList<Guid> order,
            CancellationToken ct = default
        )
        {
            Calls++;
            Orders.Add(order);
            return Task.FromResult(PluginMusicQueueAnswer.Accepted);
        }
    }
}
