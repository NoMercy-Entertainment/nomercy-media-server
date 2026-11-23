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
using Moq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api.Services.Music;

/// <summary>
/// A player says its crossfade is over: the server's state goes to the new
/// track, at the position that track is playing at.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicCrossfadeCompletionTests : IDisposable
{
    private const string Device = "phone";

    private readonly User _user = new() { Id = Guid.NewGuid(), Name = "listener" };
    private readonly MusicPlayerStateManager _states = new();
    private readonly Mock<IClientMessenger> _messenger = new();
    private readonly MusicPlaybackService _service;

    private readonly PlaylistTrackDto _playing = Track("playing");
    private readonly PlaylistTrackDto _next = Track("next");
    private readonly PlaylistTrackDto _later = Track("later");

    public MusicCrossfadeCompletionTests()
    {
        _messenger
            .Setup(clients =>
                clients.SendTo(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<object?>()
                )
            )
            .Returns(Task.CompletedTask);
        _service = new(_states, Mock.Of<IServiceProvider>(), _messenger.Object, new());
    }

    public void Dispose() => _service.RemoveTimer(_user.Id);

    [Fact]
    public async Task Without_a_position_the_new_track_starts_at_zero_as_before()
    {
        MusicPlayerState state = Fading();

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            Device,
            _next.Id
        );

        completion.Should().Be(MusicCrossfadeCompletion.Completed);
        state.CurrentItem.Should().BeSameAs(_next);
        state.Time.Should().Be(0);
        state.Playlist.Should().Equal(_later);
        state.Backlog.Should().Equal(_playing);
    }

    [Fact]
    public async Task With_a_position_the_new_track_goes_on_from_there()
    {
        MusicPlayerState state = Fading();
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            Device,
            _next.Id,
            42_000
        );

        completion.Should().Be(MusicCrossfadeCompletion.Completed);
        state.CurrentItem.Should().BeSameAs(_next);
        state.Time.Should().Be(42_000);
        state.PositionCapturedAtMs.Should().BeGreaterThanOrEqualTo(before);
        state.PlayState.Should().BeTrue();
    }

    /// <summary>
    /// The next PrepareCrossfade is measured from the new track's own end, so
    /// the flag that says it was sent has to be down again.
    /// </summary>
    [Fact]
    public async Task The_join_is_over_and_the_next_one_can_be_announced()
    {
        MusicPlayerState state = Fading();

        await _service.CompleteCrossfade(_user, Device, _next.Id, 42_000);

        state.IsCrossfading.Should().BeFalse();
        state.CrossfadeDeviceId.Should().BeNull();
        state.CrossfadeSignalSent.Should().BeFalse();
    }

    [Fact]
    public async Task The_new_state_is_broadcast_with_the_position()
    {
        Fading();
        int broadcastPosition = -1;
        _messenger
            .Setup(clients =>
                clients.SendTo("MusicPlayerState", "musicHub", _user.Id, It.IsAny<object?>())
            )
            .Callback(() => broadcastPosition = _states.GetState(_user.Id)!.Time)
            .Returns(Task.CompletedTask);

        await _service.CompleteCrossfade(_user, Device, _next.Id, 42_000);

        broadcastPosition.Should().Be(42_000);
    }

    [Theory]
    [InlineData(180_000)]
    [InlineData(1)]
    public async Task A_position_inside_the_track_is_taken(int positionMs)
    {
        MusicPlayerState state = Fading();

        (await _service.CompleteCrossfade(_user, Device, _next.Id, positionMs))
            .Should()
            .Be(MusicCrossfadeCompletion.Completed);
        state.Time.Should().Be(positionMs);
    }

    [Fact]
    public async Task A_position_past_the_end_of_the_new_track_is_refused_and_nothing_changes()
    {
        MusicPlayerState state = Fading();

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            Device,
            _next.Id,
            180_001
        );

        completion.Should().Be(MusicCrossfadeCompletion.PositionOutsideTrack);
        state.CurrentItem.Should().BeSameAs(_playing);
        state.Playlist.Should().Equal(_next, _later);
        state.IsCrossfading.Should().BeTrue();
        _messenger.VerifyNoOtherCalls();
    }

    /// <summary>
    /// With repeat all and nothing left to play, the server names the first
    /// played track as the next one. The player fades into it and says so.
    /// </summary>
    [Fact]
    public async Task With_repeat_all_the_track_that_wraps_round_is_accepted()
    {
        MusicPlayerState state = Fading();
        state.Repeat = "all";
        state.Playlist = [];
        state.Backlog = [_next, _later];

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            Device,
            _next.Id,
            5000
        );

        completion.Should().Be(MusicCrossfadeCompletion.Completed);
        state.CurrentItem.Should().BeSameAs(_next);
        state.Time.Should().Be(5000);
        state.Playlist.Should().Equal(_later, _playing);
        state.Backlog.Should().BeEmpty();
        state.PlayState.Should().BeTrue();
    }

    /// <summary>Only the first played track comes next, and only when repeat all wraps.</summary>
    [Theory]
    [InlineData("off")]
    [InlineData("one")]
    public async Task Without_repeat_all_a_played_track_is_not_the_next_one(string repeat)
    {
        MusicPlayerState state = Fading();
        state.Repeat = repeat;
        state.Playlist = [];
        state.Backlog = [_next];

        (await _service.CompleteCrossfade(_user, Device, _next.Id))
            .Should()
            .Be(MusicCrossfadeCompletion.UnknownTrack);
        state.CurrentItem.Should().BeSameAs(_playing);
    }

    [Fact]
    public async Task A_track_that_does_not_come_next_ends_the_hold_and_changes_nothing_else()
    {
        MusicPlayerState state = Fading();

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            Device,
            Guid.NewGuid(),
            1000
        );

        completion.Should().Be(MusicCrossfadeCompletion.UnknownTrack);
        state.CurrentItem.Should().BeSameAs(_playing);
        state.IsCrossfading.Should().BeFalse();
        state.CrossfadeDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task Another_device_than_the_one_that_fades_is_ignored()
    {
        MusicPlayerState state = Fading();

        MusicCrossfadeCompletion completion = await _service.CompleteCrossfade(
            _user,
            "tv",
            _next.Id,
            42_000
        );

        completion.Should().Be(MusicCrossfadeCompletion.Ignored);
        state.CurrentItem.Should().BeSameAs(_playing);
        state.IsCrossfading.Should().BeTrue();
    }

    [Fact]
    public async Task A_listener_who_plays_nothing_is_ignored()
    {
        (await _service.CompleteCrossfade(_user, Device, _next.Id, 42_000))
            .Should()
            .Be(MusicCrossfadeCompletion.Ignored);
    }

    private MusicPlayerState Fading()
    {
        MusicPlayerState state = new()
        {
            CurrentItem = _playing,
            Playlist = [_next, _later],
            PlayState = true,
            DeviceId = Device,
            CrossfadeSignalSent = true,
        };
        state.SetPosition(178_000);
        _states.UpdateState(_user.Id, state);
        _service.StartCrossfade(_user.Id, Device, 3000);

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
