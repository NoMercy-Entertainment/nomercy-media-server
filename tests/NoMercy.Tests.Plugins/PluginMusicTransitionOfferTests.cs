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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Player;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin offers audio for the join between two tracks: only a plugin the
/// owner let do so, only a file in its own folder, and only values the
/// players can be handed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginMusicTransitionOfferTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    private static readonly Guid Listener = Guid.NewGuid();

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        $"nomercy-transition-{Guid.NewGuid():N}"
    );

    private readonly Broker _broker = new();
    private readonly Host _host = new();

    public PluginMusicTransitionOfferTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "joins"));
        File.WriteAllBytes(Path.Combine(_folder, "joins", "a-b.m4a"), [1, 2, 3]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task An_offer_for_a_file_in_the_plugins_own_folder_reaches_the_player()
    {
        PluginMusicTransition transition = Offer() with { Data = """{"bars":8}""" };

        PluginMusicTransitionAnswer answer = await Player()
            .OfferTransitionAsync(Listener, transition);

        answer.Should().Be(PluginMusicTransitionAnswer.Accepted);
        _host.Offers.Should().Equal((PluginId, Listener, transition));
    }

    /// <summary>
    /// Arranging the listener's queue, and having the server serve a file:
    /// the two things an offer does, so both capabilities are asked for.
    /// </summary>
    [Fact]
    public async Task An_offer_asks_for_player_queue_and_media_proxy()
    {
        await Player().OfferTransitionAsync(Listener, Offer());

        _broker
            .Asked.Should()
            .Equal(PluginCapabilityNames.PlayerQueue, PluginCapabilityNames.MediaProxy);
    }

    [Theory]
    [InlineData(PluginCapabilityNames.PlayerQueue)]
    [InlineData(PluginCapabilityNames.MediaProxy)]
    public async Task Without_either_capability_the_offer_is_refused_and_the_player_not_asked(
        string missing
    )
    {
        _broker.Missing = missing;

        Func<Task> offer = () => Player().OfferTransitionAsync(Listener, Offer());

        (await offer.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Why.Should()
            .Contain(missing);
        _host.Offers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_that_is_not_there_is_refused_by_name()
    {
        Func<Task> offer = () =>
            Player().OfferTransitionAsync(Listener, Offer() with { File = "joins/gone.m4a" });

        (await offer.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Why.Should()
            .Contain("joins/gone.m4a");
        _host.Offers.Should().BeEmpty();
    }

    /// <summary>The server reads the file for the listener, so the folder's edge is the guard.</summary>
    [Theory]
    [InlineData("../outside.m4a")]
    [InlineData("joins/../../outside.m4a")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task A_file_outside_the_plugins_own_folder_is_refused(string file)
    {
        Func<Task> offer = () =>
            Player().OfferTransitionAsync(Listener, Offer() with { File = file });

        (await offer.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.FileOutsideGrant);
        _host.Offers.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1, 0, 1000)]
    [InlineData(0, -1, 1000)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, PluginMusicTransition.MaxDurationMs + 1)]
    public async Task Positions_and_a_length_that_cannot_be_are_a_mistake_in_the_call(
        int outgoingStopMs,
        int incomingResumeMs,
        int durationMs
    )
    {
        PluginMusicTransition transition = Offer() with
        {
            OutgoingStopMs = outgoingStopMs,
            IncomingResumeMs = incomingResumeMs,
            DurationMs = durationMs,
        };

        Func<Task> offer = () => Player().OfferTransitionAsync(Listener, transition);

        await offer.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _host.Offers.Should().BeEmpty();
    }

    /// <summary>
    /// The data travels inside a message to every client of the listener.
    /// Anything but one JSON object would break that message for all of them.
    /// </summary>
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{\"open\":")]
    [InlineData("")]
    public async Task Data_that_is_not_one_json_object_is_a_mistake_in_the_call(string data)
    {
        Func<Task> offer = () =>
            Player().OfferTransitionAsync(Listener, Offer() with { Data = data });

        await offer.Should().ThrowAsync<ArgumentException>();
        _host.Offers.Should().BeEmpty();
    }

    [Fact]
    public async Task Data_longer_than_the_limit_is_a_mistake_in_the_call()
    {
        string data = $$"""{"text":"{{new string('a', PluginMusicTransition.MaxDataLength)}}"}""";

        Func<Task> offer = () =>
            Player().OfferTransitionAsync(Listener, Offer() with { Data = data });

        await offer.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_host_that_wired_no_file_access_refuses_by_name()
    {
        PluginMusicPlayer player = new(PluginId, () => _broker, () => _host);

        Func<Task> offer = () => player.OfferTransitionAsync(Listener, Offer());

        (await offer.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.What.Should()
            .Contain("IPluginContext.Player.Music");
        _host.Offers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_withdrawal_asks_for_player_queue_and_names_the_plugin()
    {
        await Player().WithdrawTransitionAsync(Listener);

        _host.Withdrawals.Should().Equal((PluginId, Listener));
        _broker.Asked.Should().Equal(PluginCapabilityNames.PlayerQueue);
    }

    [Fact]
    public async Task A_withdrawal_without_the_capability_is_refused()
    {
        _broker.Missing = PluginCapabilityNames.PlayerQueue;

        Func<Task> withdraw = () => Player().WithdrawTransitionAsync(Listener);

        await withdraw.Should().ThrowAsync<PluginRefusedException>();
        _host.Withdrawals.Should().BeEmpty();
    }

    private PluginMusicPlayer Player() =>
        new(
            PluginId,
            () => _broker,
            () => _host,
            _folder,
            TestStorageHelper.CreateStorage(_folder)
        );

    private static PluginMusicTransition Offer() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "joins/a-b.m4a", 170_000, 12_000, 20_000);

    private sealed class Broker : IPluginCapabilityBroker
    {
        public string? Missing { get; set; }

        public List<string> Asked { get; } = [];

        public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null)
        {
            Asked.Add(capability);

            return capability == Missing
                ? new(
                    PluginRefusalCodes.CapabilityNotDeclared,
                    "joiner",
                    "The plugin offered a transition.",
                    $"It did not declare the {capability} capability.",
                    "Declare it in the manifest.",
                    PluginRefusalSeverity.Blocked
                )
                : null;
        }
    }

    private sealed class Host : IPluginMusicPlayerHost
    {
        public List<(
            Ulid PluginId,
            Guid UserId,
            PluginMusicTransition Transition
        )> Offers { get; } = [];

        public List<(Ulid PluginId, Guid UserId)> Withdrawals { get; } = [];

        public PluginMusicPlayback? Playback(Guid userId) => null;

        public Task<PluginMusicQueueAnswer> MoveInQueueAsync(
            Guid userId,
            Guid trackId,
            int position,
            CancellationToken ct = default
        ) => Task.FromResult(PluginMusicQueueAnswer.NothingPlaying);

        public Task<PluginMusicQueueAnswer> ReorderQueueAsync(
            Guid userId,
            IReadOnlyList<Guid> order,
            CancellationToken ct = default
        ) => Task.FromResult(PluginMusicQueueAnswer.NothingPlaying);

        public Task<PluginMusicTransitionAnswer> OfferTransitionAsync(
            Ulid pluginId,
            Guid userId,
            PluginMusicTransition transition,
            CancellationToken ct = default
        )
        {
            Offers.Add((pluginId, userId, transition));
            return Task.FromResult(PluginMusicTransitionAnswer.Accepted);
        }

        public Task WithdrawTransitionAsync(
            Ulid pluginId,
            Guid userId,
            CancellationToken ct = default
        )
        {
            Withdrawals.Add((pluginId, userId));
            return Task.CompletedTask;
        }
    }
}
