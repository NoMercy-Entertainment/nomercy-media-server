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

using System.Reflection;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

/// <summary>
/// Tests for the wired Suspend/Resume behavior added in the live-transcode
/// adaptive-buffer pass: Suspend cancels the runner CTS; Resume spawns a new
/// runner from the current playback position.
/// </summary>
public class LiveSessionSuspendResumeTests
{
    private static LiveQuality MakeQuality() =>
        new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 8000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    // ──────────────────────────────────────────────────────────────────────────
    // Suspend cancels the runner CTS
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Suspend_WhenTranscoding_CancelsRunnerCancellationToken()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);

        CancellationToken runnerToken = session.RunnerCancellation;

        session.Suspend();

        // The original runner token must be cancelled so FFmpeg exits.
        runnerToken.IsCancellationRequested.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Suspend idempotence: a second Suspend while already Buffered is a no-op
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Suspend_WhenAlreadyBuffered_DoesNotChangeState()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Buffered);

        session.Suspend();

        session.State.Should().Be(LiveSessionState.Buffered);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resume spawns a new runner from the playback position
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_AfterSuspend_SpawnsNewRunner()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);

        TimeSpan reportedPosition = TimeSpan.FromSeconds(42);
        session.ReportPlaybackPosition(reportedPosition, authoritative: true);

        TimeSpan? spawnedPosition = null;
        session.AttachRunnerFactory(
            (pos, _) =>
            {
                spawnedPosition = pos;
                return Task.CompletedTask;
            }
        );

        session.Suspend();
        session.Resume();

        // Give the fire-and-forget Task a moment to execute
        await Task.Delay(50);

        spawnedPosition.Should().NotBeNull();
        spawnedPosition!.Value.Should().Be(reportedPosition);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resume without a factory wired (no-runner path) still flips state
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resume_NoFactory_FlipsStateWithoutThrowing()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Buffered);

        Action act = () => session.Resume();

        act.Should().NotThrow();
        session.State.Should().Be(LiveSessionState.Transcoding);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resume idempotence: a second Resume while already Transcoding is a no-op
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_WhenAlreadyTranscoding_DoesNotSpawnSecondRunner()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);

        int spawnCount = 0;
        session.AttachRunnerFactory(
            (_, _) =>
            {
                spawnCount++;
                return Task.CompletedTask;
            }
        );

        session.Resume();

        await Task.Delay(50);

        spawnCount.Should().Be(0);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Resume issues a fresh CTS after Suspend
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_AfterSuspend_RunnerTokenIsNotCancelled()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);
        session.AttachRunnerFactory((_, _) => Task.CompletedTask);

        session.Suspend();

        CancellationToken oldToken = session.RunnerCancellation;
        oldToken.IsCancellationRequested.Should().BeTrue();

        session.Resume();

        await Task.Delay(50);

        // After resume, a fresh CTS is installed — new token must not be cancelled
        session.RunnerCancellation.IsCancellationRequested.Should().BeFalse();
    }

    // ── Resume/Suspend routed through _seekLock ──────────────────────────────
    //
    // Resume()/Suspend() used to mutate _runnerCts OUTSIDE the _seekLock that
    // SeekAsync/ChangeQualityAsync hold — a Resume racing a Seek could read
    // and replace _runnerCts out from under the other, leaking a zombie
    // ffmpeg the session no longer tracks. Both are now gated by the SAME
    // _seekLock SeekAsync uses.

    private static CancellationTokenSource GetRunnerCts(LiveSession session)
    {
        FieldInfo field = typeof(LiveSession).GetField(
            "_runnerCts",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;
        return (CancellationTokenSource)field.GetValue(session)!;
    }

    [Fact]
    public void Resume_DisposesThePreviousRunnerCts()
    {
        // The CTS Suspend() cancels is not disposed by Suspend() itself (it
        // may still be the session's live CTS if Resume() is never called).
        // Resume() replacing it must dispose it instead of leaking it.
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);
        session.AttachRunnerFactory((_, _) => Task.CompletedTask);

        session.Suspend();
        CancellationTokenSource suspendedCts = GetRunnerCts(session);

        session.Resume();

        Action act = () => _ = suspendedCts.Token;
        act.Should()
            .Throw<ObjectDisposedException>(
                "Resume() must dispose the CTS Suspend() cancelled instead of leaking it"
            );
    }

    // The hook these two use to prove "Seek is holding _seekLock" changed, not
    // the contract under test. SeekAsync still cancels the CURRENT runner token
    // (oldCts.CancelAsync()) WHILE holding _seekLock, before releasing it in its
    // finally — that cancellation dispatches every callback registered on that
    // token, so a callback that blocks keeps SeekAsync from reaching its
    // finally/release. Registering a blocking callback on the token via the
    // PUBLIC RunnerCancellation property (instead of the old buffer-reset-
    // callback hook, which Seek no longer invokes) proves the same real
    // contract through the public API — a concurrent Suspend/Resume genuinely
    // cannot replace _runnerCts out from under an in-progress Seek.

    [Fact]
    public async Task Suspend_ConcurrentWithSeek_WaitsForSeekToReleaseTheLock()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Transcoding);

        using ManualResetEventSlim seekIsHoldingLock = new(false);
        using ManualResetEventSlim releaseSeek = new(false);
        session.RunnerCancellation.Register(() =>
        {
            seekIsHoldingLock.Set();
            releaseSeek.Wait(TimeSpan.FromSeconds(5));
        });

        Task seekTask = Task.Run(() =>
            session.SeekAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
        );

        seekIsHoldingLock.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task suspendTask = Task.Run(() => session.Suspend());
        await Task.Delay(150);
        suspendTask
            .IsCompleted.Should()
            .BeFalse("Suspend must wait for the in-progress Seek to release _seekLock");

        releaseSeek.Set();
        await Task.WhenAll(seekTask, suspendTask).WaitAsync(TimeSpan.FromSeconds(5));

        suspendTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task Resume_ConcurrentWithSeek_WaitsForSeekToReleaseTheLock()
    {
        LiveSession session = new(Ulid.NewUlid().ToString(), MakeQuality());
        session.SetState(LiveSessionState.Buffered);

        using ManualResetEventSlim seekIsHoldingLock = new(false);
        using ManualResetEventSlim releaseSeek = new(false);
        session.RunnerCancellation.Register(() =>
        {
            seekIsHoldingLock.Set();
            releaseSeek.Wait(TimeSpan.FromSeconds(5));
        });

        Task seekTask = Task.Run(() =>
            session.SeekAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
        );

        seekIsHoldingLock.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task resumeTask = Task.Run(() => session.Resume());
        await Task.Delay(150);
        resumeTask
            .IsCompleted.Should()
            .BeFalse("Resume must wait for the in-progress Seek to release _seekLock");

        releaseSeek.Set();
        await Task.WhenAll(seekTask, resumeTask).WaitAsync(TimeSpan.FromSeconds(5));

        resumeTask.IsCompletedSuccessfully.Should().BeTrue();
    }
}
