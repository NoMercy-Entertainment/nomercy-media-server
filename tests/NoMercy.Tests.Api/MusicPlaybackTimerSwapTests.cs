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

using Moq;
using NoMercy.Api.DTOs.Music;
using NoMercy.Api.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Messaging;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// StartPlaybackTimer is reached from two unrelated locks at once: hub commands
/// hold MusicHub's per-user command lock, while the tick's own auto-advance
/// (HandleTrackCompletion, CompleteCrossfade) holds the state lock. Two
/// overlapping starts must leave exactly one timer behind — an orphan that
/// fell out of the timer map is never disposed by RemoveTimer and keeps adding
/// 100ms to Time on every tick, so the position runs at double speed.
/// </summary>
[Trait("Category", "Unit")]
public class MusicPlaybackTimerSwapTests
{
    private const int ConcurrentStartRounds = 200;

    private static PlaylistTrackDto MakeTrack()
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            Duration = "3600",
            Filename = "test.mp3",
            Folder = "/music/",
            FolderId = Ulid.NewUlid(),
        };
        return new(track, "US");
    }

    private static (
        MusicPlaybackService Service,
        MusicPlayerStateManager StateManager
    ) MakeService()
    {
        MusicPlayerStateManager stateManager = new();
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

        MusicPlaybackService service = new(
            stateManager,
            Mock.Of<IServiceProvider>(),
            messenger.Object,
            new MusicActiveDeviceRegistry()
        );

        return (service, stateManager);
    }

    [Fact]
    public async Task StartPlaybackTimer_FromTwoThreadsAtOnce_LeavesNoTimerBehindAfterRemoveTimer()
    {
        (MusicPlaybackService service, MusicPlayerStateManager stateManager) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };

        // No DeviceId: the staleness watchdog only judges a session that names an
        // active device, so the tick does nothing but advance Time here.
        MusicPlayerState state = new()
        {
            PlayState = true,
            CurrentItem = MakeTrack(),
            CurrentList = new("/music/albums/test", UriKind.Relative),
        };
        stateManager.UpdateState(userId, state);

        try
        {
            using Barrier roundStart = new(2);
            Task StartRepeatedly()
            {
                return Task.Run(() =>
                {
                    for (int round = 0; round < ConcurrentStartRounds; round++)
                    {
                        roundStart.SignalAndWait();
                        service.StartPlaybackTimer(user);
                    }
                });
            }

            await Task.WhenAll(StartRepeatedly(), StartRepeatedly());
        }
        finally
        {
            service.RemoveTimer(userId);
        }

        // Let any tick that was already mid-flight when the timer was disposed
        // finish before taking the reference position.
        await Task.Delay(300);
        int timeAfterRemoval = state.Time;

        await Task.Delay(500);

        state
            .Time.Should()
            .Be(
                timeAfterRemoval,
                "RemoveTimer must stop every timer StartPlaybackTimer ever created for the user"
            );
    }
}
