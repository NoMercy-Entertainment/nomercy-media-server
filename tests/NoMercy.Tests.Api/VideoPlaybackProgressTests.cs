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

using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services.Video;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// The server used to run its own 100ms playback clock and write watch progress
/// from it. It kept counting while paused and long after every client had
/// closed, and because the same counter decided end-of-item it also jumped a
/// viewer to the next episode halfway through the one they were watching.
/// These cover the replacement: the player that renders the video owns the
/// position, and nothing moves it but a report from that player.
/// </summary>
[Trait("Category", "Unit")]
public class VideoPlaybackProgressTests
{
    private const int OneSecondMs = 1_000;
    private const string TwentyFiveMinutes = "25:00";
    private static readonly int TwentyFiveMinutesMs = 25 * 60 * 1_000;

    private static User MakeUser()
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Name = "tester",
            Email = "tester@example.com",
        };
    }

    private static VideoPlayerState MakePlayingState(int itemCount = 2, int currentIndex = 0)
    {
        List<VideoPlaylistResponseDto> playlist = [];
        for (int id = 1; id <= itemCount; id++)
        {
            playlist.Add(new() { Id = id, Duration = TwentyFiveMinutes });
        }

        // The service locates the current item by reference, so it has to be the
        // playlist entry itself; an equal-looking copy is never found in the list.
        return new()
        {
            PlayState = true,
            CurrentList = new("/tv/1399/watch", UriKind.Relative),
            Actions = new(),
            CurrentItem = playlist[currentIndex],
            Playlist = playlist,
        };
    }

    private static (VideoPlaybackService Service, VideoPlayerStateManager States) MakeService()
    {
        VideoPlayerStateManager states = new();
        Mock<IClientMessenger> messenger = new();
        messenger
            .Setup(m =>
                m.SendTo(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Guid>(),
                    It.IsAny<object?>()
                )
            )
            .Returns(Task.CompletedTask);

        VideoPlaybackService service = new(
            states,
            Mock.Of<IServiceScopeFactory>(),
            messenger.Object
        );

        return (service, states);
    }

    [Fact]
    public async Task AReportedPositionIsTheSessionPosition()
    {
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState();
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, 90 * OneSecondMs);

        state.Time.Should().Be(90 * OneSecondMs);
    }

    [Fact]
    public async Task NothingMovesThePositionBetweenReports()
    {
        // The whole bug: with the apps closed the row kept climbing about a
        // second per second, because a timer — not a player — owned this value.
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState();
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, 60 * OneSecondMs);
        await Task.Delay(TimeSpan.FromSeconds(2));

        state.Time.Should().Be(60 * OneSecondMs);
    }

    [Fact]
    public async Task APausedPlayerThatStopsReportingKeepsItsPosition()
    {
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState();
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, 42 * OneSecondMs);
        state.PlayState = false;
        await Task.Delay(TimeSpan.FromSeconds(2));

        state.Time.Should().Be(42 * OneSecondMs);
    }

    [Fact]
    public async Task HalfwayThroughAnEpisodeIsNotTheEndOfIt()
    {
        // Reported live: half an episode in, the next one started.
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState();
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, TwentyFiveMinutesMs / 2);

        state.CurrentItem!.Id.Should().Be(1);
    }

    [Fact]
    public async Task ReachingTheEndAdvancesToTheNextItem()
    {
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState();
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, TwentyFiveMinutesMs);

        state.CurrentItem.Should().BeSameAs(state.Playlist[1]);
    }

    [Fact]
    public async Task FinishingAnItemPlaysTheOneRightAfterIt()
    {
        // Three episodes and the first one ends: the second must play, not the third.
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState(itemCount: 3, currentIndex: 0);
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, TwentyFiveMinutesMs);

        state.CurrentItem.Should().BeSameAs(state.Playlist[1]);
    }

    [Fact]
    public async Task FinishingTheSecondToLastItemPlaysTheLastOne()
    {
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState(itemCount: 3, currentIndex: 1);
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, TwentyFiveMinutesMs);

        state.CurrentItem.Should().BeSameAs(state.Playlist[2]);
    }

    [Fact]
    public async Task FinishingTheLastItemEndsTheSession()
    {
        (VideoPlaybackService service, VideoPlayerStateManager states) = MakeService();
        User user = MakeUser();
        VideoPlayerState state = MakePlayingState(itemCount: 3, currentIndex: 2);
        states.UpdateState(user.Id, state);

        await service.ApplyClientProgress(user, state, TwentyFiveMinutesMs);

        state.CurrentItem.Should().BeNull();
        state.Playlist.Should().BeEmpty();
        states.HasState(user.Id).Should().BeFalse();
    }
}
