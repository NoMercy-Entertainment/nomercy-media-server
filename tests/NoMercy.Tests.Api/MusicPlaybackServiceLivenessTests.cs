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
/// Covers the liveness-based active-device release: MusicPlaybackService must
/// author who is active by proof-of-life, not by "is the WebSocket still
/// connected" — the root cause of a device (backgrounded TV, crashed player,
/// anonymous Cast Receiver) holding a music session forever after it stops
/// actually producing audio.
/// </summary>
[Trait("Category", "Unit")]
public class MusicPlaybackServiceLivenessTests
{
    // provenAlive defaults true: every case below models a device that HAD been
    // reporting and went quiet, which is what the short timeout exists to find.
    // A device that has never reported once gets a longer window and is covered
    // separately.
    private static MusicPlayerState MakePlayingState(
        string? deviceId,
        DateTime lastHeartbeatUtc,
        bool provenAlive = true
    )
    {
        return new()
        {
            DeviceId = deviceId,
            PlayState = true,
            CurrentList = new("/music/albums/test", UriKind.Relative),
            LastActiveHeartbeatUtc = lastHeartbeatUtc,
            ActiveDeviceProvenAlive = provenAlive,
        };
    }

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
        MusicActiveDeviceRegistry Registry,
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

        return (service, stateManager, registry, messenger);
    }

    // ── IsActiveDeviceStale: a device that has never reported yet ────────────
    // Measured on a Nokia Streaming Box woken from sleep by Cast: handed
    // playback at 11:24:46, first audio sample at 11:25:04. The short timeout
    // ended the session at 11:25:01 — three seconds before the music reached the
    // speakers — so a cast that had worked was heard as one that failed.

    [Fact]
    public void IsActiveDeviceStale_False_WhileAJustHandedDeviceIsStillComingUp()
    {
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState(
            "tv-waking-from-sleep",
            now.AddMilliseconds(-(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1_000)),
            provenAlive: false
        );

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeFalse();
    }

    [Fact]
    public void IsActiveDeviceStale_True_WhenAJustHandedDeviceNeverComesUpAtAll()
    {
        // The longer window is patience, not surrender: a device that never
        // arrives still has its session ended, just later.
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState(
            "tv-that-never-woke",
            now.AddMilliseconds(-(MusicPlaybackService.ActiveDeviceFirstReportTimeoutMs + 1)),
            provenAlive: false
        );

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeTrue();
    }

    [Fact]
    public void IsActiveDeviceStale_True_AtTheShortTimeout_OnceTheDeviceHasReportedOnce()
    {
        // The first heartbeat is what narrows the window. A device that proved
        // itself and then died must still be found in fifteen seconds, not
        // forty-five — the longer window must not leak into steady state.
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState("device-a", now, provenAlive: false);

        MusicPlaybackService.TryRefreshHeartbeat(state, "device-a").Should().BeTrue();
        state.ActiveDeviceProvenAlive.Should().BeTrue();

        state.LastActiveHeartbeatUtc = now.AddMilliseconds(
            -(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1)
        );

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeTrue();
    }

    [Fact]
    public void TryRefreshHeartbeat_FromAPassiveCaller_DoesNotMarkTheActiveDeviceProven()
    {
        // A passive device's report must not narrow the window on behalf of an
        // active device that still hasn't said anything.
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow, provenAlive: false);

        MusicPlaybackService.TryRefreshHeartbeat(state, "device-b").Should().BeFalse();
        state.ActiveDeviceProvenAlive.Should().BeFalse();
    }

    // ── IsActiveDeviceStale ──────────────────────────────────────────────────

    [Fact]
    public void IsActiveDeviceStale_False_WhenHeartbeatIsRecent()
    {
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState("device-a", now.AddSeconds(-5));

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeFalse();
    }

    [Fact]
    public void IsActiveDeviceStale_True_WhenHeartbeatExceedsTimeout()
    {
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState(
            "device-a",
            now.AddMilliseconds(-(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1))
        );

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeTrue();
    }

    [Fact]
    public void IsActiveDeviceStale_False_AtExactTimeoutBoundary()
    {
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState(
            "device-a",
            now.AddMilliseconds(-MusicPlaybackService.ActiveDeviceStaleTimeoutMs)
        );

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeFalse();
    }

    [Fact]
    public void IsActiveDeviceStale_False_WhenPaused_RegardlessOfHeartbeatAge()
    {
        // A deliberate pause/stop must never be treated as staleness —
        // MusicPlaybackCommandHandler.HandleStop relies on the active device
        // staying sticky through a stop to block hijack from a passive tap.
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState("device-a", now.AddHours(-1));
        state.PlayState = false;

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeFalse();
    }

    [Fact]
    public void IsActiveDeviceStale_False_WhenNoActiveDeviceRecorded()
    {
        DateTime now = DateTime.UtcNow;
        MusicPlayerState state = MakePlayingState(null, now.AddHours(-1));

        MusicPlaybackService.IsActiveDeviceStale(state, now).Should().BeFalse();
    }

    // ── IsCallerTheActiveDevice ──────────────────────────────────────────────

    [Fact]
    public void IsCallerTheActiveDevice_True_WhenCallerMatchesActiveDeviceId()
    {
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow);

        MusicPlaybackService.IsCallerTheActiveDevice(state, "device-a").Should().BeTrue();
    }

    [Fact]
    public void IsCallerTheActiveDevice_True_IsCaseInsensitive()
    {
        MusicPlayerState state = MakePlayingState("Device-A", DateTime.UtcNow);

        MusicPlaybackService.IsCallerTheActiveDevice(state, "device-a").Should().BeTrue();
    }

    [Fact]
    public void IsCallerTheActiveDevice_False_WhenCallerIsAPassiveDevice()
    {
        // A stray report from a device that is NOT active must never refresh
        // the real active device's clock — that would mask a truly-dead device.
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow);

        MusicPlaybackService.IsCallerTheActiveDevice(state, "device-b").Should().BeFalse();
    }

    [Fact]
    public void IsCallerTheActiveDevice_False_WhenCallerDeviceIdIsNull()
    {
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow);

        MusicPlaybackService.IsCallerTheActiveDevice(state, null).Should().BeFalse();
    }

    [Fact]
    public void IsCallerTheActiveDevice_False_WhenNoActiveDeviceRecorded()
    {
        MusicPlayerState state = MakePlayingState(null, DateTime.UtcNow);

        MusicPlaybackService.IsCallerTheActiveDevice(state, "device-a").Should().BeFalse();
    }

    // ── TryRefreshHeartbeat ───────────────────────────────────────────────────
    // The single gate MusicHub routes ReportPositionCommand/CurrentTimeCommand,
    // PlaybackCommand, and GetStateCommand through. Covers both effects it must
    // have together: proof-of-life for the staleness sweep, and (via its bool
    // result) permission for the caller to move the authoritative position —
    // a passive caller must get neither.

    [Fact]
    public void TryRefreshHeartbeat_RefreshesHeartbeatAndReturnsTrue_WhenCallerIsActiveDevice()
    {
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow.AddSeconds(-10));

        bool result = MusicPlaybackService.TryRefreshHeartbeat(state, "device-a");

        result.Should().BeTrue();
        state.LastActiveHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryRefreshHeartbeat_IsCaseInsensitive()
    {
        MusicPlayerState state = MakePlayingState("Device-A", DateTime.UtcNow.AddSeconds(-10));

        MusicPlaybackService.TryRefreshHeartbeat(state, "device-a").Should().BeTrue();
    }

    [Fact]
    public void TryRefreshHeartbeat_LeavesHeartbeatUntouched_AndReturnsFalse_WhenCallerIsPassive()
    {
        // A passive mirror's own report — e.g. a device that was just demoted by
        // ChangeDeviceCommand and hasn't yet stopped its own reporting loop — must
        // be a complete no-op: it may neither extend the active device's grace
        // window nor (per the caller's use of the false return) overwrite the
        // authoritative position with its own.
        DateTime lastHeartbeat = DateTime.UtcNow.AddSeconds(-10);
        MusicPlayerState state = MakePlayingState("device-a", lastHeartbeat);

        MusicPlaybackService.TryRefreshHeartbeat(state, "device-b").Should().BeFalse();

        state.LastActiveHeartbeatUtc.Should().Be(lastHeartbeat);
    }

    [Fact]
    public void TryRefreshHeartbeat_ReturnsFalse_WhenCallerDeviceIdIsNull()
    {
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow.AddSeconds(-10));

        MusicPlaybackService.TryRefreshHeartbeat(state, null).Should().BeFalse();
    }

    [Fact]
    public void TryRefreshHeartbeat_ReturnsFalse_WhenNoActiveDeviceRecorded()
    {
        MusicPlayerState state = MakePlayingState(null, DateTime.UtcNow.AddSeconds(-10));

        MusicPlaybackService.TryRefreshHeartbeat(state, "device-a").Should().BeFalse();
    }

    // ── EndStaleActiveSessionAsync ───────────────────────────────────────────

    [Fact]
    public async Task EndStaleActiveSessionAsync_ClearsSessionAndReleasesActiveDevice()
    {
        (
            MusicPlaybackService service,
            _,
            MusicActiveDeviceRegistry registry,
            Mock<IClientMessenger> messenger
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "stale-device", Type = "web" });

        MusicPlayerState state = MakePlayingState("stale-device", DateTime.UtcNow.AddSeconds(-30));
        state.CurrentItem = MakeTrack();
        state.Playlist = [MakeTrack()];
        state.Backlog = [MakeTrack()];

        await service.EndStaleActiveSessionAsync(user, state);

        state.CurrentItem.Should().BeNull();
        state.PlayState.Should().BeFalse();
        state.DeviceId.Should().BeNull();
        state.Time.Should().Be(0);
        state.Playlist.Should().BeEmpty();
        state.Backlog.Should().BeEmpty();
        state.Actions.Disallows.Resuming.Should().BeTrue();
        state.Actions.Disallows.Pausing.Should().BeTrue();

        registry.TryGet(userId, out _).Should().BeFalse();

        messenger.Verify(
            m => m.SendTo("MusicPlayerState", "musicHub", userId, It.IsAny<object>()),
            Times.Once
        );
    }

    [Fact]
    public async Task EndStaleActiveSessionAsync_DoesNotReleaseADeviceThatAlreadyTookOver()
    {
        // A device switch (ChangeDeviceCommand) raced in between the staleness
        // detection and the release actually running — the switch must win.
        (MusicPlaybackService service, _, MusicActiveDeviceRegistry registry, _) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "new-device", Type = "web" });

        MusicPlayerState state = MakePlayingState("stale-device", DateTime.UtcNow.AddSeconds(-30));
        state.CurrentItem = MakeTrack();

        await service.EndStaleActiveSessionAsync(user, state);

        registry.TryGet(userId, out Device? found).Should().BeTrue();
        found!.DeviceId.Should().Be("new-device");
    }

    // ── Real timer wiring ────────────────────────────────────────────────────
    // Proves StartPlaybackTimer's own tick actually reaches the staleness
    // sweep end to end, not just that the extracted predicate is correct in
    // isolation. Pre-ages the heartbeat past the timeout BEFORE the timer
    // starts so the very first ~100ms tick already observes it as stale —
    // no need to wait out the real 15s window.

    [Fact]
    public async Task StartPlaybackTimer_EndsSession_WhenActiveDeviceIsAlreadyStaleAtStart()
    {
        (
            MusicPlaybackService service,
            MusicPlayerStateManager stateManager,
            MusicActiveDeviceRegistry registry,
            _
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "zombie-device", Type = "web" });

        MusicPlayerState state = MakePlayingState("zombie-device", DateTime.UtcNow);
        state.CurrentItem = MakeTrack();
        stateManager.UpdateState(userId, state);

        try
        {
            // StartPlaybackTimer itself stamps a fresh heartbeat (that's the
            // resume-after-pause fix below), so back-date it AFTER starting —
            // simulating time passing with no further position reports, the
            // actual real-world staleness path — rather than fighting that stamp.
            service.StartPlaybackTimer(user);
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddMilliseconds(
                -(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1_000)
            );

            MusicPlayerState? observed = null;
            for (
                int attempt = 0;
                attempt < 40 && (observed is null || observed.CurrentItem is not null);
                attempt++
            )
            {
                await Task.Delay(50);
                stateManager.TryGetValue(userId, out observed);
            }

            observed.Should().NotBeNull();
            observed!.CurrentItem.Should().BeNull();
            observed.PlayState.Should().BeFalse();
            registry.TryGet(userId, out _).Should().BeFalse();
        }
        finally
        {
            service.RemoveTimer(userId);
        }
    }

    [Fact]
    public async Task StartPlaybackTimer_ResetsHeartbeat_SoAResumeAfterALongPauseSurvives()
    {
        // Regression guard: a legitimate resume after sitting paused for far
        // longer than the stale timeout must NOT immediately look stale to the
        // very next tick — StartPlaybackTimer must stamp a fresh heartbeat
        // whenever it (re)arms the loop.
        (
            MusicPlaybackService service,
            MusicPlayerStateManager stateManager,
            MusicActiveDeviceRegistry registry,
            _
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "device-a", Type = "web" });

        MusicPlayerState state = MakePlayingState(
            "device-a",
            DateTime.UtcNow.AddHours(-1) // long stale-looking pause
        );
        state.CurrentItem = MakeTrack();
        stateManager.UpdateState(userId, state);

        try
        {
            service.StartPlaybackTimer(user);

            await Task.Delay(300);

            stateManager.TryGetValue(userId, out MusicPlayerState? observed);
            observed.Should().NotBeNull();
            observed!
                .CurrentItem.Should()
                .NotBeNull("resuming after a normal pause must not end the session");
            observed.PlayState.Should().BeTrue();
        }
        finally
        {
            service.RemoveTimer(userId);
        }
    }

    // ── BeginPlaybackStart / EndPlaybackStart (StartPlaybackCommand in-flight guard) ──
    // SignalR dispatches invocations on one connection serially by default, so a slow
    // playlist fetch inside StartPlaybackCommand starves that connection's queued
    // position reports for its whole duration — the watchdog must not kill the session
    // out from under a client that is still alive and simply queued behind its own
    // start command, but a genuinely dead device must still be caught once the flag
    // clears.

    [Fact]
    public void BeginPlaybackStart_StampsFreshHeartbeat_ForExistingSession()
    {
        (MusicPlaybackService service, MusicPlayerStateManager stateManager, _, _) = MakeService();

        Guid userId = Guid.NewGuid();
        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow.AddHours(-1));
        stateManager.UpdateState(userId, state);

        service.BeginPlaybackStart(userId);

        state.LastActiveHeartbeatUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));

        service.EndPlaybackStart(userId);
    }

    [Fact]
    public void IsPlaybackStartInFlight_FalseBeforeBegin_TrueAfterBegin_FalseAfterEnd()
    {
        (MusicPlaybackService service, _, _, _) = MakeService();
        Guid userId = Guid.NewGuid();

        service.IsPlaybackStartInFlight(userId).Should().BeFalse();

        service.BeginPlaybackStart(userId);
        service.IsPlaybackStartInFlight(userId).Should().BeTrue();

        service.EndPlaybackStart(userId);
        service.IsPlaybackStartInFlight(userId).Should().BeFalse();
    }

    [Fact]
    public async Task StartPlaybackTimer_DoesNotEndSession_WhilePlaybackStartIsInFlight_EvenIfHeartbeatIsStale()
    {
        (
            MusicPlaybackService service,
            MusicPlayerStateManager stateManager,
            MusicActiveDeviceRegistry registry,
            _
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "zombie-device", Type = "web" });

        MusicPlayerState state = MakePlayingState("zombie-device", DateTime.UtcNow);
        state.CurrentItem = MakeTrack();
        stateManager.UpdateState(userId, state);

        try
        {
            service.BeginPlaybackStart(userId);
            service.StartPlaybackTimer(user);

            // Simulate the long GetPlaylist fetch: no further reports arrive and the
            // heartbeat ages well past ActiveDeviceStaleTimeoutMs while the flag is set.
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddMilliseconds(
                -(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1_000)
            );

            await Task.Delay(300);

            stateManager.TryGetValue(userId, out MusicPlayerState? observed);
            observed.Should().NotBeNull();
            observed!
                .CurrentItem.Should()
                .NotBeNull("a start in flight must never look stale to the watchdog");
            observed.PlayState.Should().BeTrue();
            registry.TryGet(userId, out _).Should().BeTrue();
        }
        finally
        {
            service.EndPlaybackStart(userId);
            service.RemoveTimer(userId);
        }
    }

    // ── WithStateLockAsync (ReportPositionCoreAsync / ReportPositionForItemCommand guard) ──
    // Before this existed, a position report mutated MusicPlayerState and
    // broadcast it with no lock at all — the one hub call that took neither
    // GetUserLock (PlaybackCommand/StartPlaybackCommand) nor this state lock
    // (StartPlaybackTimer's own 100ms tick). A report landing mid-tick, while
    // HandleTrackCompletion/UpdateStateBasedOnRepeatMode was partway through
    // its several sequential field writes, could broadcast a torn combination
    // of fields from before and after that mutation.

    [Fact]
    public async Task WithStateLockAsync_SerializesConcurrentCallsForTheSameUser()
    {
        (MusicPlaybackService service, _, _, _) = MakeService();
        Guid userId = Guid.NewGuid();

        TaskCompletionSource<bool> firstEntered = new();
        TaskCompletionSource<bool> releaseFirst = new();
        int concurrentCount = 0;
        bool overlapped = false;

        Task first = service.WithStateLockAsync(
            userId,
            async () =>
            {
                Interlocked.Increment(ref concurrentCount);
                firstEntered.SetResult(true);
                await releaseFirst.Task;
                Interlocked.Decrement(ref concurrentCount);
            }
        );

        await firstEntered.Task;

        Task second = service.WithStateLockAsync(
            userId,
            () =>
            {
                if (Interlocked.Increment(ref concurrentCount) > 1)
                    overlapped = true;
                Interlocked.Decrement(ref concurrentCount);
                return Task.CompletedTask;
            }
        );

        // Give the second call every chance to (wrongly) run while the first
        // still holds the lock, before releasing it.
        await Task.Delay(100);
        releaseFirst.SetResult(true);

        await Task.WhenAll(first, second);

        overlapped
            .Should()
            .BeFalse(
                "a position report and the playback timer's own tick must never mutate MusicPlayerState at the same time"
            );
    }

    [Fact]
    public async Task WithStateLockAsync_SerializesAgainstTheRealPlaybackTimersOwnTick()
    {
        // End-to-end version of the test above: proves the SAME lock instance
        // StartPlaybackTimer's real Timer callback acquires (via GetStateLock)
        // is the one WithStateLockAsync acquires too, not two different locks
        // that happen to share a name. A report call blocked here for the
        // whole delay would mean the two paths are sharing state without
        // sharing a lock — exactly the bug this closes.
        (
            MusicPlaybackService service,
            MusicPlayerStateManager stateManager,
            MusicActiveDeviceRegistry registry,
            _
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "device-a", Type = "web" });

        MusicPlayerState state = MakePlayingState("device-a", DateTime.UtcNow);
        state.CurrentItem = MakeTrack();
        stateManager.UpdateState(userId, state);

        try
        {
            service.StartPlaybackTimer(user);

            bool ranUnderLock = false;
            await service.WithStateLockAsync(
                userId,
                () =>
                {
                    ranUnderLock = true;
                    return Task.CompletedTask;
                }
            );

            ranUnderLock.Should().BeTrue();
        }
        finally
        {
            service.RemoveTimer(userId);
        }
    }

    [Fact]
    public async Task StartPlaybackTimer_EndsSession_OnceInFlightFlagClears_IfStillStale()
    {
        // A genuinely dead device must still be caught on the very next cadence once
        // the in-flight flag clears — the guard above only defers detection for the
        // duration of a legitimate start, it must never suppress it permanently.
        (
            MusicPlaybackService service,
            MusicPlayerStateManager stateManager,
            MusicActiveDeviceRegistry registry,
            _
        ) = MakeService();

        Guid userId = Guid.NewGuid();
        User user = new() { Id = userId, Name = "Test User" };
        registry.Set(userId, new() { DeviceId = "zombie-device", Type = "web" });

        MusicPlayerState state = MakePlayingState("zombie-device", DateTime.UtcNow);
        state.CurrentItem = MakeTrack();
        stateManager.UpdateState(userId, state);

        try
        {
            service.BeginPlaybackStart(userId);
            service.StartPlaybackTimer(user);
            state.LastActiveHeartbeatUtc = DateTime.UtcNow.AddMilliseconds(
                -(MusicPlaybackService.ActiveDeviceStaleTimeoutMs + 1_000)
            );

            // The flag clears — mirrors StartPlaybackCommand's finally block once its
            // (slow) work finishes without ever receiving a fresh report.
            service.EndPlaybackStart(userId);

            MusicPlayerState? observed = null;
            for (
                int attempt = 0;
                attempt < 40 && (observed is null || observed.CurrentItem is not null);
                attempt++
            )
            {
                await Task.Delay(50);
                stateManager.TryGetValue(userId, out observed);
            }

            observed.Should().NotBeNull();
            observed!.CurrentItem.Should().BeNull();
            observed.PlayState.Should().BeFalse();
            registry.TryGet(userId, out _).Should().BeFalse();
        }
        finally
        {
            service.RemoveTimer(userId);
        }
    }
}
