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

using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NoMercy.Encoder.LiveTranscode;

public class LiveSession : ILiveSession
{
    // How long an authoritative playhead report (a real client heartbeat, a
    // seek, or the encode start position) stays in force before a
    // non-authoritative report — the segment-request-derived prefetch
    // frontier — is allowed to move the playhead again. Without this window a
    // player that prefetches far ahead of what the user is actually watching
    // overwrites the true playhead on every segment GET, and BufferAhead reads
    // as permanently low even though the encoder is racing far past the
    // viewer's real position.
    private static readonly TimeSpan AuthoritativePlayheadWindow = TimeSpan.FromSeconds(15);

    private readonly Channel<Segment> _segmentChannel = Channel.CreateUnbounded<Segment>();

    // Protects concurrent seeks: only one seek can manipulate the runner CTS at a time.
    private readonly SemaphoreSlim _seekLock = new(1, 1);

    // Tracks the current runner's cancellation source. Replaced on each seek.
    private CancellationTokenSource _runnerCts = new();

    // Linked to _runnerCts; cancelled when the whole session is disposed.
    private readonly CancellationTokenSource _sessionCts = new();

    private int _state = (int)LiveSessionState.Starting;
    private long _playbackPositionTicks;

    // Client-reported network health: download-buffer depth (seconds of media
    // downloaded but not yet played) and observed downlink in kbps. MinValue
    // ticks below means "no report yet" — HasFreshClientHealth reads a huge age
    // against that default, so an old client that never reports keeps the
    // network axis skipped, exactly like the playhead fields above.
    private long _clientBufferedAheadTicks;
    private int _observedBandwidthKbps;
    private long _lastClientHealthUtcTicks = DateTime.MinValue.Ticks;

    // UTC ticks of the last authoritative playhead report. MinValue-relative
    // default (0) means "no authoritative report yet" — DateTime.UtcNow minus
    // DateTime(0) is always well outside AuthoritativePlayheadWindow, so a
    // non-authoritative report applies freely until the first authoritative
    // one lands (session start always reports one; see LiveEncoder).
    private long _lastAuthoritativePlayheadUtcTicks;
    private TimeSpan _transcodedPosition;
    private double _currentSpeed;
    private int _audioStreamIndex;

    // MinValue until the first runner is dispatched, so a session that never
    // spawns one (unit tests exercising Evaluate directly) is not treated as
    // "warming up". Stamped on every real (re)spawn via MarkTranscodeStart.
    private long _lastTranscodeStartTicks = DateTime.MinValue.Ticks;

    // The media position the current runner encodes from; -1 when unknown.
    private long _runnerFromTicks = -1;

    // Injected by LiveEncoder after construction via AttachRunnerFactory.
    private Func<TimeSpan, CancellationToken, Task>? _runnerFactory;

    // The most recently spawned runner task (LiveEncoder's initial spawn, or a
    // Seek/ChangeQuality/Resume respawn). Cancelling _runnerCts only asks
    // LiveFfmpegRunner to stop — the actual ffmpeg process kill and the
    // synchronous WaitForExit() that releases its file handles happen inside
    // that task. DisposeAsync must await it, or scratch-directory cleanup
    // races the still-exiting process and fails with "used by another
    // process" (see LiveStreamingService.TryDeleteScratchAsync).
    private Task? _currentRunnerTask;

    internal void TrackRunnerTask(Task task) => Volatile.Write(ref _currentRunnerTask, task);

    // Injected by LiveStreamingService via AttachBufferResetCallback.
    private Action? _bufferResetCallback;

    /// <summary>
    /// Fires when the CURRENT runner should terminate. Replaced on each seek.
    /// The outer session lifetime is tracked via <see cref="_sessionCts"/>.
    /// </summary>
    public CancellationToken RunnerCancellation => _runnerCts.Token;

    public string SessionId { get; }
    public LiveSessionState State =>
        (LiveSessionState)Interlocked.CompareExchange(ref _state, 0, 0);
    public LiveQuality CurrentQuality { get; private set; }
    public int CurrentAudioStreamIndex => Volatile.Read(ref _audioStreamIndex);
    public double CurrentSpeed => _currentSpeed;
    public TimeSpan TranscodedPosition => _transcodedPosition;
    public TimeSpan BufferAhead =>
        _transcodedPosition - new TimeSpan(Interlocked.Read(ref _playbackPositionTicks));

    public IAsyncEnumerable<Segment> Segments => ReadSegmentsAsync();

    public LiveSession(string sessionId, LiveQuality quality)
    {
        SessionId = sessionId;
        CurrentQuality = quality;
    }

    public void AttachRunnerFactory(Func<TimeSpan, CancellationToken, Task> factory)
    {
        _runnerFactory = factory;
    }

    public void AttachBufferResetCallback(Action callback)
    {
        _bufferResetCallback = callback;
    }

    // Called by the encoder to push completed segments into the channel
    internal void PushSegment(Segment segment)
    {
        _transcodedPosition = segment.StartTime + segment.Duration;
        _segmentChannel.Writer.TryWrite(segment);
    }

    internal void SetState(LiveSessionState state) => Interlocked.Exchange(ref _state, (int)state);

    // Updates the reported quality in place, WITHOUT tearing down/restarting the
    // runner. Used by LiveFfmpegRunner's NVENC-session-cap fallback: the runner
    // has already substituted a software encoder for the CURRENT run, so the
    // session's public quality just needs to catch up for API/UI reporting and
    // so a later seek/resume respawn (which reads CurrentQuality) keeps using
    // the software encoder instead of retrying the exhausted GPU slot.
    internal void SetQuality(LiveQuality quality) => CurrentQuality = quality;

    // Sets the audio track a subsequent (re)spawn maps. Called at session start
    // with the track resolved from the library's language preference.
    internal void SetAudioStreamIndex(int index) => Volatile.Write(ref _audioStreamIndex, index);

    internal void SetSpeed(double speed) => _currentSpeed = speed;

    internal void Complete() => _segmentChannel.Writer.TryComplete();

    /// <summary>
    /// Completes the segment channel only when <paramref name="runnerToken"/>
    /// still belongs to the CURRENT runner generation. A seek / quality-change
    /// / resume replaces <see cref="_runnerCts"/> with a fresh source before
    /// the runner it is replacing has necessarily exited; that superseded
    /// runner's own completion must not end the session's segment stream out
    /// from under the runner that replaced it. Called by
    /// <see cref="LiveFfmpegRunner.RunAsync"/>'s finally block instead of
    /// <see cref="Complete"/> directly.
    /// </summary>
    internal void CompleteIfCurrentRunner(CancellationToken runnerToken)
    {
        try
        {
            if (_runnerCts.Token != runnerToken)
                return;
        }
        catch (ObjectDisposedException)
        {
            // Session is already tearing down — DisposeAsync owns completing
            // the channel in that case.
            return;
        }

        Complete();
    }

    /// <summary>
    /// Transitions <see cref="LiveSessionState.Transcoding"/> to
    /// <see cref="LiveSessionState.Buffered"/> when <paramref name="runnerToken"/>
    /// still belongs to the CURRENT runner generation — same superseded-generation
    /// guard as <see cref="CompleteIfCurrentRunner"/>. Called by
    /// <see cref="LiveFfmpegRunner.RunAsync"/>'s finally block instead of
    /// <see cref="CompleteIfCurrentRunner"/> when the run was bounded
    /// (<c>LiveRunInput.StopPosition</c> set): it finished the gap it was spawned
    /// to fill, not the file, so the segment channel must stay open for a later
    /// seek/resume to reuse — completing it here would end the whole session.
    /// </summary>
    internal void MarkRunnerIdle(CancellationToken runnerToken)
    {
        try
        {
            if (_runnerCts.Token != runnerToken)
                return;
        }
        catch (ObjectDisposedException)
        {
            // Session is already tearing down — DisposeAsync owns the final state.
            return;
        }

        Interlocked.CompareExchange(
            ref _state,
            (int)LiveSessionState.Buffered,
            (int)LiveSessionState.Transcoding
        );
    }

    /// <summary>
    /// Cancels the current FFmpeg runner, resets position state, then spawns
    /// a new runner from <paramref name="position"/> via the attached factory.
    /// </summary>
    public async Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The running FFmpeg already reaches this spot soon (the app seeks to
            // its resume point right after the session starts): keep it, so its
            // warm-up is not thrown away. Only the playhead moves.
            if (RunnerWillReach(position))
            {
                Interlocked.Exchange(ref _playbackPositionTicks, position.Ticks);
                Interlocked.Exchange(ref _lastAuthoritativePlayheadUtcTicks, DateTime.UtcNow.Ticks);
                return;
            }

            SetState(LiveSessionState.Seeking);

            // Tear down existing runner
            CancellationTokenSource oldCts = _runnerCts;
            try
            {
                await oldCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                oldCts.Dispose();
            }

            // Reset position bookkeeping. A seek is an authoritative reposition —
            // stamp the authority window too so a stale, in-flight prefetch report
            // (issued for the pre-seek position, landing just after) cannot
            // immediately clobber the seek target.
            Interlocked.Exchange(ref _playbackPositionTicks, position.Ticks);
            Interlocked.Exchange(ref _lastAuthoritativePlayheadUtcTicks, DateTime.UtcNow.Ticks);
            _transcodedPosition = position;

            // Create a new CTS for the replacement runner, linked to session lifetime
            _runnerCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);

            // Deliberately NOT invoking _bufferResetCallback here. A seek changes
            // only the playhead — same quality, same absolute segment indices,
            // deterministic content — so nothing on disk or in the runtime buffer
            // is invalidated. Wiping it here is what used to erase the encoder's
            // coverage knowledge and made every seek re-encode ground a previous
            // runner generation had already produced. See ChangeQualityAsync,
            // which still resets: a quality change genuinely invalidates the
            // existing segments.

            // Spawn new runner if a factory is wired up
            if (_runnerFactory is not null)
            {
                SetState(LiveSessionState.Transcoding);
                MarkTranscodeStart(position);

                TrackRunnerTask(
                    Task.Run(
                        () => _runnerFactory(position, _runnerCts.Token),
                        CancellationToken.None
                    )
                );
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    public async Task ChangeQualityAsync(
        string qualityId,
        LiveQuality newQuality,
        CancellationToken ct
    )
    {
        await _seekLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _state, (int)LiveSessionState.ChangingQuality);

            // Tear down existing runner
            CancellationTokenSource oldCts = _runnerCts;
            try
            {
                await oldCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
            finally
            {
                oldCts.Dispose();
            }

            // Update quality before spawning so the factory closure picks it up
            CurrentQuality = newQuality;

            // Create a new CTS for the replacement runner, linked to session lifetime
            _runnerCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);

            _bufferResetCallback?.Invoke();

            // Spawn new runner if a factory is wired up
            if (_runnerFactory is not null)
            {
                SetState(LiveSessionState.Transcoding);

                // Keep same playback position — quality change doesn't rewind
                TimeSpan resumePosition = new(Interlocked.Read(ref _playbackPositionTicks));
                MarkTranscodeStart(resumePosition);
                TrackRunnerTask(
                    Task.Run(
                        () => _runnerFactory(resumePosition, _runnerCts.Token),
                        CancellationToken.None
                    )
                );
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    /// <summary>
    /// Blocking acquire on the same <see cref="_seekLock"/> SeekAsync /
    /// ChangeQualityAsync await into asynchronously. Suspend/Resume are
    /// synchronous by contract (SignalR hub methods and the buffer-adaptive
    /// sweep call them without awaiting), so they take the lock via the
    /// synchronous companion instead of changing that contract — without it,
    /// a Resume racing a Seek could each read/replace _runnerCts out from
    /// under the other and leak a zombie ffmpeg the session no longer tracks.
    /// </summary>
    public void Suspend()
    {
        _seekLock.Wait();
        try
        {
            int previous = Interlocked.CompareExchange(
                ref _state,
                (int)LiveSessionState.Buffered,
                (int)LiveSessionState.Transcoding
            );

            if (previous != (int)LiveSessionState.Transcoding)
                return;

            try
            {
                _runnerCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed; nothing to cancel
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    public void Resume()
    {
        _seekLock.Wait();
        try
        {
            int previous = Interlocked.CompareExchange(
                ref _state,
                (int)LiveSessionState.Transcoding,
                (int)LiveSessionState.Buffered
            );

            if (previous != (int)LiveSessionState.Buffered)
                return;

            // The CTS being replaced here was cancelled by the matching
            // Suspend() call but never disposed — Dispose() is idempotent so
            // this is safe even if something else already disposed it.
            _runnerCts.Dispose();

            _runnerCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);

            if (_runnerFactory is not null)
            {
                TimeSpan resumePosition = new(Interlocked.Read(ref _playbackPositionTicks));
                MarkTranscodeStart(resumePosition);
                TrackRunnerTask(
                    Task.Run(
                        () => _runnerFactory(resumePosition, _runnerCts.Token),
                        CancellationToken.None
                    )
                );
            }
        }
        finally
        {
            _seekLock.Release();
        }
    }

    /// <summary>
    /// Updates the playhead used to compute <see cref="BufferAhead"/>.
    /// <paramref name="authoritative"/> true (a client heartbeat, a seek, or the
    /// encode start position) always applies and refreshes the authority window;
    /// false (the segment-request-derived prefetch frontier) applies only when
    /// no authoritative report has landed within <see cref="AuthoritativePlayheadWindow"/>,
    /// so a live client's true position is never overwritten by how far ahead
    /// the player has prefetched.
    /// </summary>
    public void ReportPlaybackPosition(TimeSpan position, bool authoritative)
    {
        if (authoritative)
        {
            Interlocked.Exchange(ref _playbackPositionTicks, position.Ticks);
            Interlocked.Exchange(ref _lastAuthoritativePlayheadUtcTicks, DateTime.UtcNow.Ticks);
            return;
        }

        DateTime lastAuthoritative = new(
            Interlocked.Read(ref _lastAuthoritativePlayheadUtcTicks),
            DateTimeKind.Utc
        );
        if (DateTime.UtcNow - lastAuthoritative > AuthoritativePlayheadWindow)
            Interlocked.Exchange(ref _playbackPositionTicks, position.Ticks);
    }

    public DateTime LastTranscodeStart =>
        new(Interlocked.Read(ref _lastTranscodeStartTicks), DateTimeKind.Utc);

    // How far past the encoded frontier a seek may land and still wait for the
    // current runner instead of respawning it.
    internal static readonly TimeSpan SeekReuseLead = TimeSpan.FromSeconds(10);

    private bool RunnerWillReach(TimeSpan target)
    {
        long fromTicks = Interlocked.Read(ref _runnerFromTicks);
        if (fromTicks < 0 || _runnerFactory is null || _runnerCts.IsCancellationRequested)
            return false;
        LiveSessionState state = State;
        if (state is not (LiveSessionState.Starting or LiveSessionState.Transcoding))
            return false;
        TimeSpan from = new(fromTicks);
        TimeSpan frontier = _transcodedPosition > from ? _transcodedPosition : from;
        return target >= from && target <= frontier + SeekReuseLead;
    }

    public void MarkTranscodeStart(TimeSpan? from = null)
    {
        Interlocked.Exchange(ref _lastTranscodeStartTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _runnerFromTicks, from?.Ticks ?? -1);
    }

    public TimeSpan ClientBufferedAhead => new(Interlocked.Read(ref _clientBufferedAheadTicks));

    public int ObservedBandwidthKbps => Volatile.Read(ref _observedBandwidthKbps);

    public void ReportClientBufferHealth(TimeSpan bufferedAhead, int observedBandwidthKbps)
    {
        Interlocked.Exchange(ref _clientBufferedAheadTicks, bufferedAhead.Ticks);
        Volatile.Write(ref _observedBandwidthKbps, observedBandwidthKbps);
        Interlocked.Exchange(ref _lastClientHealthUtcTicks, DateTime.UtcNow.Ticks);
    }

    public bool HasFreshClientHealth(TimeSpan maxAge)
    {
        DateTime lastReport = new(
            Interlocked.Read(ref _lastClientHealthUtcTicks),
            DateTimeKind.Utc
        );
        return DateTime.UtcNow - lastReport <= maxAge;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _sessionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        try
        {
            _runnerCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        // Cancelling _runnerCts only signals LiveFfmpegRunner to stop; the
        // actual process kill + WaitForExit() that releases ffmpeg's file
        // handles run inside the tracked task. Waiting for it here (bounded,
        // since ProcessRunner already caps its own graceful-kill grace period
        // at 5s) is what lets LiveStreamingService.TryDeleteScratchAsync delete the
        // scratch directory without racing a still-exiting process.
        Task? runnerTask = Volatile.Read(ref _currentRunnerTask);
        if (runnerTask is not null)
        {
            try
            {
                await Task.WhenAny(runnerTask, Task.Delay(TimeSpan.FromSeconds(10)))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
            }
        }

        SetState(LiveSessionState.Ended);
        _segmentChannel.Writer.TryComplete();

        _seekLock.Dispose();
        _runnerCts.Dispose();
        _sessionCts.Dispose();
    }

    private async IAsyncEnumerable<Segment> ReadSegmentsAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        await foreach (Segment segment in _segmentChannel.Reader.ReadAllAsync(ct))
        {
            yield return segment;
        }
    }
}
