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
/// Covers the server-authoritative broadcast sequencing added to stop clients
/// racing duplicate/out-of-order MusicPlayerState broadcasts: the per-user
/// monotonic, clock-anchored <see cref="MusicPlayerStateManager.NextSeq"/>, the
/// additive <see cref="MusicPlayerState.Seq"/> wire field it stamps, and the
/// conservative burst-coalescing in
/// <see cref="MusicPlaybackService.UpdatePlaybackState"/>.
/// </summary>
[Trait("Category", "Unit")]
public class MusicBroadcastSequencingTests
{
    private static PlaylistTrackDto MakeTrack()
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Track",
            Duration = "180",
            Filename = "test.mp3",
            Folder = "/music/",
            FolderId = Ulid.NewUlid(),
        };
        return new(track, "US");
    }

    private static (
        MusicPlaybackService Service,
        MusicPlayerStateManager StateManager,
        Mock<IClientMessenger> Messenger
    ) MakeService()
    {
        MusicPlayerStateManager stateManager = new();
        MusicActiveDeviceRegistry registry = new();
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
            registry
        );

        return (service, stateManager, messenger);
    }

    // ── MusicPlayerStateManager.NextSeq ──────────────────────────────────────

    [Fact]
    public void NextSeq_IsStrictlyIncreasing_AcrossRapidCallsForSameUser()
    {
        MusicPlayerStateManager stateManager = new();
        Guid userId = Guid.NewGuid();

        long previous = stateManager.NextSeq(userId);
        for (int i = 0; i < 1_000; i++)
        {
            long next = stateManager.NextSeq(userId);
            next.Should().BeGreaterThan(previous);
            previous = next;
        }
    }

    [Fact]
    public void NextSeq_IsClockAnchored_ForTheFirstCallOfAFreshManager()
    {
        // Simulates a server restart: a brand-new MusicPlayerStateManager (no
        // history of prior sequence numbers) must still anchor to the wall
        // clock rather than restart from a low counter a stale client could
        // already have seen and surpassed.
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        MusicPlayerStateManager stateManager = new();

        long seq = stateManager.NextSeq(Guid.NewGuid());

        seq.Should().BeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public void NextSeq_TracksUsersIndependently()
    {
        MusicPlayerStateManager stateManager = new();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        // Drive userA's counter far ahead of the wall clock — enough rapid
        // same-millisecond calls each fall back to previousSeq + 1.
        long userALastSeq = 0;
        for (int i = 0; i < 10_000; i++)
            userALastSeq = stateManager.NextSeq(userA);

        long beforeUserBCall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long userBFirstSeq = stateManager.NextSeq(userB);

        // userB's first call must not inherit userA's inflated counter — it
        // stays anchored to the wall clock instead of a shared sequence.
        userBFirstSeq.Should().BeLessThan(userALastSeq);
        userBFirstSeq.Should().BeGreaterThanOrEqualTo(beforeUserBCall);
    }

    // ── MusicPlaybackService.UpdatePlaybackState — Seq stamping ─────────────

    [Fact]
    public async Task UpdatePlaybackState_StampsSeq_OnTheBroadcastState()
    {
        (MusicPlaybackService service, _, _) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        MusicPlayerState state = new() { CurrentList = new("", UriKind.Relative) };

        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await service.UpdatePlaybackState(user, state);

        state.Seq.Should().BeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task UpdatePlaybackState_SeqIsStrictlyIncreasing_AcrossDistinctBroadcasts()
    {
        (MusicPlaybackService service, _, _) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        MusicPlayerState state = new()
        {
            CurrentItem = MakeTrack(),
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
        };

        await service.UpdatePlaybackState(user, state);
        long first = state.Seq;

        // A genuinely new position (different time bucket) must never be
        // coalesced away, so this always produces a fresh, higher Seq.
        state.SetPosition(5_000);
        await service.UpdatePlaybackState(user, state);
        long second = state.Seq;

        second.Should().BeGreaterThan(first);
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotStampSeq_WhenStateIsNull()
    {
        // There is no MusicPlayerState to carry a Seq on a clearing broadcast —
        // this must not throw and must still relay the null-state event.
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };

        await service.UpdatePlaybackState(user, null);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Once
        );
    }

    // ── MusicPlayerState.CloneForBroadcast — Seq must survive the projection ─

    [Fact]
    public void CloneForBroadcast_PreservesSeq()
    {
        MusicPlayerState state = new() { CurrentList = new("", UriKind.Relative), Seq = 123_456 };

        MusicPlayerState broadcast = state.CloneForBroadcast();

        broadcast.Seq.Should().Be(123_456);
    }

    // ── Burst coalescing ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePlaybackState_CoalescesIdenticalBroadcast_WithinWindow()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        PlaylistTrackDto track = MakeTrack();
        MusicPlayerState state = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        // Two back-to-back emits for the exact same track/play-state/second of
        // position — the redundant echo a burst of ungated call sites can
        // produce — must collapse to a single broadcast.
        await service.UpdatePlaybackState(user, state);
        long firstSeq = state.Seq;
        await service.UpdatePlaybackState(user, state);
        long afterSecondCall = state.Seq;

        afterSecondCall.Should().Be(firstSeq);
        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenPlayStateDiffers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        PlaylistTrackDto track = MakeTrack();
        MusicPlayerState state = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        await service.UpdatePlaybackState(user, state);
        state.PlayState = false; // genuine pause — must always reach clients
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenCurrentItemDiffers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        MusicPlayerState state = new()
        {
            CurrentItem = MakeTrack(),
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        await service.UpdatePlaybackState(user, state);
        state.CurrentItem = MakeTrack(); // genuine track change
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenDeviceIdDiffers()
    {
        // Regression: a device switch usually changes nothing else in the
        // fingerprint — same track, same play state, same time bucket — so
        // without DeviceId in the comparison, a ChangeDeviceCommand landing
        // inside the coalesce window looked like the redundant echo of the
        // broadcast right before it and was dropped whole. The device being
        // demoted never heard it and kept playing indefinitely.
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        PlaylistTrackDto track = MakeTrack();
        MusicPlayerState state = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
            DeviceId = "device-a",
        };

        await service.UpdatePlaybackState(user, state);
        state.DeviceId = "device-b"; // genuine handoff to a different device
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenVolumeDiffers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        MusicPlayerState state = new()
        {
            CurrentItem = MakeTrack(),
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
            VolumePercentage = 40,
        };

        await service.UpdatePlaybackState(user, state);
        state.VolumePercentage = 60; // a slider drag lands inside the window
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenAPassiveDeviceVolumeDiffers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        MusicPlayerState state = new()
        {
            CurrentItem = MakeTrack(),
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
            DeviceId = "device-a",
        };
        state.DeviceVolumes["device-b"] = 20;

        await service.UpdatePlaybackState(user, state);
        state.DeviceVolumes["device-b"] = 70; // controller moves the passive device
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_WhenTimeBucketDiffers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        PlaylistTrackDto track = MakeTrack();
        MusicPlayerState state = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        await service.UpdatePlaybackState(user, state);
        state.Time = 3_500; // a different second-granularity position
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_AcrossDifferentUsers()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        PlaylistTrackDto track = MakeTrack();
        User userA = new() { Id = Guid.NewGuid(), Name = "User A" };
        User userB = new() { Id = Guid.NewGuid(), Name = "User B" };
        MusicPlayerState stateA = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };
        MusicPlayerState stateB = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        await service.UpdatePlaybackState(userA, stateA);
        await service.UpdatePlaybackState(userB, stateB);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", userA.Id, It.IsAny<object>()),
            Times.Once
        );
        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", userB.Id, It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task UpdatePlaybackState_DoesNotCoalesce_AfterTheWindowElapses()
    {
        (MusicPlaybackService service, _, Mock<IClientMessenger> messenger) = MakeService();
        User user = new() { Id = Guid.NewGuid(), Name = "Test User" };
        PlaylistTrackDto track = MakeTrack();
        MusicPlayerState state = new()
        {
            CurrentItem = track,
            CurrentList = new("", UriKind.Relative),
            PlayState = true,
            Time = 1_500,
        };

        await service.UpdatePlaybackState(user, state);
        // Comfortably past the ~50ms coalesce window — a legitimately repeated
        // tick of the same fields must still reach clients.
        await Task.Delay(120);
        await service.UpdatePlaybackState(user, state);

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", user.Id, It.IsAny<object>()),
            Times.Exactly(2)
        );
    }
}
