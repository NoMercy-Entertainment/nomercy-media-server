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
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Hubs.Shared;
using NoMercy.Api.Services.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Networking.Messaging;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

/// <summary>
/// What the music player answers a plugin: what plays for a listener, and the
/// listener's upcoming tracks put in another order.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginMusicPlayerHostTests : IDisposable
{
    private readonly User _user = new() { Id = Guid.NewGuid(), Name = "listener" };
    private readonly MusicPlayerStateManager _states = new();
    private readonly List<MusicPlayerState> _broadcasts = [];
    private readonly ServiceProvider _services;
    private readonly PluginMusicPlayerHost _host;
    private readonly MusicPlaybackService _playback;
    private readonly InMemoryEventBus _bus = new();
    private readonly MusicPlaybackAnnouncer _announcer;

    private readonly PlaylistTrackDto _playing = Track("playing");
    private readonly PlaylistTrackDto _first = Track("first");
    private readonly PlaylistTrackDto _second = Track("second");
    private readonly PlaylistTrackDto _third = Track("third");

    public PluginMusicPlayerHostTests()
    {
        Mock<IClientMessenger> messenger = new();
        messenger
            .Setup(clients =>
                clients.SendTo("MusicPlayerState", "musicHub", _user.Id, It.IsAny<object?>())
            )
            .Callback(
                (string _, string _, Guid _, object? payload) =>
                {
                    EventPayload<PlayerStateEventElement<MusicPlayerState, MusicEventType>> sent =
                        (EventPayload<PlayerStateEventElement<MusicPlayerState, MusicEventType>>)
                            payload!;
                    _broadcasts.Add(sent.Events[0].Event.State!);
                }
            )
            .Returns(Task.CompletedTask);

        Mock<IUserRepository> users = new();
        users.Setup(repository => repository.GetByIdAsync(_user.Id)).ReturnsAsync(_user);

        _services = new ServiceCollection().AddSingleton(users.Object).BuildServiceProvider();
        _announcer = new(_bus);
        _playback = new(_states, _services, messenger.Object, new(), announcer: _announcer);
        _host = new(_states, _playback, _services.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _announcer.Dispose();
        _services.Dispose();
    }

    [Fact]
    public void A_listener_who_plays_nothing_has_no_playback()
    {
        _host.Playback(_user.Id).Should().BeNull();

        _states.UpdateState(_user.Id, new());

        _host.Playback(_user.Id).Should().BeNull();
    }

    [Fact]
    public void What_plays_is_read_with_its_queue_and_modes()
    {
        MusicPlayerState state = Play();
        state.Repeat = "all";
        state.Shuffle = true;
        state.Time = 42_000;
        state.CurrentList = new("/music/playlist/abc", UriKind.Relative);

        _host
            .Playback(_user.Id)
            .Should()
            .BeEquivalentTo(
                new PluginMusicPlayback(
                    _user.Id,
                    _playing.Id,
                    "phone",
                    true,
                    42_000,
                    [_first.Id, _second.Id, _third.Id],
                    "all",
                    true
                )
                {
                    CurrentList = "/music/playlist/abc",
                }
            );
    }

    [Fact]
    public async Task A_queued_track_moved_to_the_front_is_the_next_track()
    {
        MusicPlayerState state = Play();

        PluginMusicQueueAnswer answer = await _host.MoveInQueueAsync(_user.Id, _third.Id, 0);

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        state.Playlist.Should().Equal(_third, _first, _second);
        MusicPlaybackService.PeekNextTrack(state).Should().BeSameAs(_third);
    }

    /// <summary>Listeners look at their queue, so every client is shown the new order.</summary>
    [Fact]
    public async Task A_move_is_broadcast_to_the_listeners_players_in_the_new_order()
    {
        Play();

        await _host.MoveInQueueAsync(_user.Id, _third.Id, 0);

        _broadcasts
            .Should()
            .ContainSingle()
            .Which.Playlist.Select(track => track.Id)
            .Should()
            .Equal(_third.Id, _first.Id, _second.Id);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public async Task A_track_is_moved_to_the_place_asked_and_no_further_than_the_end(int position)
    {
        MusicPlayerState state = Play();

        await _host.MoveInQueueAsync(_user.Id, _first.Id, position);

        List<PlaylistTrackDto> expected = [_second, _third];
        expected.Insert(Math.Min(position, 2), _first);
        state.Playlist.Should().Equal(expected);
    }

    [Fact]
    public async Task A_track_that_is_not_queued_is_not_moved()
    {
        MusicPlayerState state = Play();

        PluginMusicQueueAnswer answer = await _host.MoveInQueueAsync(_user.Id, Guid.NewGuid(), 0);
        PluginMusicQueueAnswer playing = await _host.MoveInQueueAsync(_user.Id, _playing.Id, 0);

        answer.Should().Be(PluginMusicQueueAnswer.NotInQueue);
        playing.Should().Be(PluginMusicQueueAnswer.NotInQueue);
        state.Playlist.Should().Equal(_first, _second, _third);
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_arranged_for_a_listener_who_plays_nothing()
    {
        (await _host.MoveInQueueAsync(_user.Id, _first.Id, 0))
            .Should()
            .Be(PluginMusicQueueAnswer.NothingPlaying);
        (await _host.ReorderQueueAsync(Guid.NewGuid(), []))
            .Should()
            .Be(PluginMusicQueueAnswer.NothingPlaying);

        _states.UpdateState(_user.Id, new() { Playlist = [_first] });

        (await _host.ReorderQueueAsync(_user.Id, [_first.Id]))
            .Should()
            .Be(PluginMusicQueueAnswer.NothingPlaying);
    }

    [Fact]
    public async Task A_track_already_in_its_place_changes_nothing_and_tells_nobody()
    {
        Play();

        PluginMusicQueueAnswer answer = await _host.MoveInQueueAsync(_user.Id, _first.Id, 0);

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        _broadcasts.Should().BeEmpty();
    }

    /// <summary>
    /// The players were told what comes next and are loading it. A change of
    /// the next track now would have them fade into the wrong one.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Another_next_track_is_refused_once_the_players_were_told_what_comes_next(
        bool signalSent,
        bool crossfading
    )
    {
        MusicPlayerState state = Play();
        state.CrossfadeSignalSent = signalSent;
        if (crossfading)
            _playback.StartCrossfade(_user.Id, "phone", 3000);

        PluginMusicQueueAnswer front = await _host.MoveInQueueAsync(_user.Id, _third.Id, 0);
        PluginMusicQueueAnswer whole = await _host.ReorderQueueAsync(
            _user.Id,
            [_second.Id, _first.Id, _third.Id]
        );

        front.Should().Be(PluginMusicQueueAnswer.TooLate);
        whole.Should().Be(PluginMusicQueueAnswer.TooLate);
        state.Playlist.Should().Equal(_first, _second, _third);
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task What_comes_after_the_next_track_can_still_be_arranged_then()
    {
        MusicPlayerState state = Play();
        state.CrossfadeSignalSent = true;

        PluginMusicQueueAnswer answer = await _host.ReorderQueueAsync(
            _user.Id,
            [_first.Id, _third.Id, _second.Id]
        );

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        state.Playlist.Should().Equal(_first, _third, _second);
    }

    [Fact]
    public async Task The_whole_queue_is_put_in_a_new_order_in_one_step()
    {
        MusicPlayerState state = Play();

        PluginMusicQueueAnswer answer = await _host.ReorderQueueAsync(
            _user.Id,
            [_third.Id, _second.Id, _first.Id]
        );

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        state.Playlist.Should().Equal(_third, _second, _first);
        _broadcasts.Should().ContainSingle();
    }

    /// <summary>
    /// A change a plugin made is said to plugins like any other: once for the
    /// whole new order, with every upcoming track in it.
    /// </summary>
    [Fact]
    public async Task A_new_order_is_said_to_plugins_once_with_the_whole_queue()
    {
        List<PluginMessageEvent> said = [];
        SemaphoreSlim published = new(0);
        using IDisposable subscription = _bus.Subscribe<PluginMessageEvent>(
            (message, _) =>
            {
                said.Add(message);
                published.Release();
                return Task.CompletedTask;
            }
        );
        MusicPlayerState state = Play();
        await _playback.UpdatePlaybackState(_user, state);

        await _host.ReorderQueueAsync(_user.Id, [_third.Id, _second.Id, _first.Id]);
        await _playback.UpdatePlaybackState(_user, state);

        (await published.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await published.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await published.WaitAsync(TimeSpan.FromMilliseconds(300))).Should().BeFalse();

        said.Select(message => message.Name)
            .Should()
            .Equal(PluginTopics.MusicPlaybackStarted, PluginTopics.MusicPlaybackQueueChanged);
        PluginMusicPlaybackQueueChanged changed = said[1]
            .Payload.Deserialize<PluginMusicPlaybackQueueChanged>(JsonSerializerOptions.Web)!;
        changed.UserId.Should().Be(_user.Id);
        changed.TrackId.Should().Be(_playing.Id);
        changed.Queue.Should().Equal(_third.Id, _second.Id, _first.Id);
    }

    /// <summary>A plugin arranges what the listener queued: it adds nothing and drops nothing.</summary>
    [Fact]
    public async Task An_order_that_is_not_the_queued_tracks_is_refused()
    {
        MusicPlayerState state = Play();
        Guid stranger = Guid.NewGuid();

        Guid[][] orders =
        [
            [_first.Id, _second.Id],
            [_first.Id, _second.Id, _third.Id, stranger],
            [_first.Id, _second.Id, stranger],
            [_first.Id, _first.Id, _second.Id],
            [],
        ];

        foreach (Guid[] order in orders)
            (await _host.ReorderQueueAsync(_user.Id, order))
                .Should()
                .Be(PluginMusicQueueAnswer.NotInQueue);

        state.Playlist.Should().Equal(_first, _second, _third);
        _broadcasts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_track_queued_twice_stays_queued_twice()
    {
        MusicPlayerState state = Play();
        state.Playlist = [_first, _second, _first];

        PluginMusicQueueAnswer answer = await _host.ReorderQueueAsync(
            _user.Id,
            [_first.Id, _first.Id, _second.Id]
        );

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        state.Playlist.Should().Equal(_first, _first, _second);
    }

    [Fact]
    public async Task The_same_order_again_changes_nothing_and_tells_nobody()
    {
        Play();

        PluginMusicQueueAnswer answer = await _host.ReorderQueueAsync(
            _user.Id,
            [_first.Id, _second.Id, _third.Id]
        );

        answer.Should().Be(PluginMusicQueueAnswer.Accepted);
        _broadcasts.Should().BeEmpty();
    }

    /// <summary>
    /// Right after another broadcast nothing but the order differs. That
    /// broadcast must still go out, or the listener keeps looking at the old
    /// queue until the next position report.
    /// </summary>
    [Fact]
    public async Task A_new_order_right_after_another_broadcast_is_still_sent()
    {
        MusicPlayerState state = Play();
        await _playback.UpdatePlaybackState(_user, state);

        await _host.ReorderQueueAsync(_user.Id, [_third.Id, _second.Id, _first.Id]);

        _broadcasts.Should().HaveCount(2);
        _broadcasts[1].Playlist.Select(track => track.Id).Should().StartWith(_third.Id);
    }

    private MusicPlayerState Play()
    {
        MusicPlayerState state = new()
        {
            CurrentItem = _playing,
            Playlist = [_first, _second, _third],
            PlayState = true,
            DeviceId = "phone",
        };
        _states.UpdateState(_user.Id, state);

        return state;
    }

    private static PlaylistTrackDto Track(string name) =>
        new(
            new Track
            {
                Id = Guid.NewGuid(),
                Name = name,
                Duration = "180",
                Filename = $"{name}.mp3",
                Folder = "/music/",
                FolderId = Ulid.NewUlid(),
            },
            "US"
        );
}
