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

namespace NoMercy.Encoder.LiveTranscode;

public interface ILiveSession : IAsyncDisposable
{
    string SessionId { get; }
    LiveSessionState State { get; }
    LiveQuality CurrentQuality { get; }

    /// <summary>
    /// Zero-based index among the source's audio streams currently being mapped
    /// (<c>0:a:N</c>). Read at every runner (re)spawn so a seek or quality change
    /// keeps the track resolved from the library's language preference.
    /// </summary>
    int CurrentAudioStreamIndex { get; }

    double CurrentSpeed { get; }
    TimeSpan TranscodedPosition { get; }
    TimeSpan BufferAhead { get; }
    IAsyncEnumerable<Segment> Segments { get; }

    /// <summary>
    /// Tears down the current FFmpeg runner and spawns a new one starting at
    /// <paramref name="position"/>. The caller receives control back once the
    /// new runner is dispatched; segment flow resumes asynchronously.
    /// </summary>
    Task SeekAsync(TimeSpan position, CancellationToken ct);

    /// <summary>
    /// Tears down the current FFmpeg runner and spawns a new one using
    /// <paramref name="newQuality"/>. Resumes from the current playback
    /// position so the viewer does not jump backward.
    /// </summary>
    Task ChangeQualityAsync(string qualityId, LiveQuality newQuality, CancellationToken ct);
    void Suspend();
    void Resume();

    /// <summary>
    /// Updates the playhead used to compute <see cref="BufferAhead"/>.
    /// <paramref name="authoritative"/> true (a client heartbeat, a seek, or the
    /// encode start position) always applies; false (the segment-request-derived
    /// prefetch frontier) applies only while no authoritative report is still
    /// within its authority window, so a live client's true position is never
    /// overwritten by how far ahead the player has prefetched.
    /// </summary>
    void ReportPlaybackPosition(TimeSpan position, bool authoritative);

    /// <summary>
    /// UTC time the current FFmpeg runner was (re)started — session start, seek,
    /// quality change, or resume. <see cref="DateTime.MinValue"/> until the first
    /// runner is spawned. The buffer-adaptive sweep uses it as a warm-up grace so
    /// it does not act on the legitimately-empty buffer of a runner that has not
    /// yet written its first segment.
    /// </summary>
    DateTime LastTranscodeStart { get; }

    /// <summary>
    /// Stamps <see cref="LastTranscodeStart"/> with the current UTC time. Called
    /// at every point a new runner is dispatched. <paramref name="from"/> is the
    /// media position the new runner encodes from, when the caller knows it.
    /// </summary>
    void MarkTranscodeStart(TimeSpan? from = null);

    /// <summary>
    /// Attaches the factory that <see cref="SeekAsync"/> uses to spawn a
    /// replacement runner. Called once by <see cref="LiveEncoder"/> immediately
    /// after the session is created.
    /// </summary>
    void AttachRunnerFactory(Func<TimeSpan, CancellationToken, Task> factory);

    /// <summary>
    /// Registers a callback invoked at the start of every quality change — NOT a
    /// seek — so the runtime buffer (and, via <c>LiveStreamingService</c>, the
    /// on-disk segments) can be purged before the new runner fires. A quality
    /// change genuinely invalidates the existing segments (different bitrate/
    /// resolution/encoder); a seek does not — same quality, same absolute segment
    /// indices, deterministic content — so <c>SeekAsync</c> deliberately does not
    /// invoke this callback. Called once by <see cref="LiveStreamingService"/>
    /// after registration.
    /// </summary>
    void AttachBufferResetCallback(Action callback);

    /// <summary>
    /// The client's last-reported download-buffer depth: seconds of media it
    /// holds downloaded but not yet played. This is a NETWORK signal, distinct
    /// from <see cref="BufferAhead"/> (an encoder-capacity signal — how far the
    /// transcoder ran ahead of the playhead). Zero until the first report.
    /// </summary>
    TimeSpan ClientBufferedAhead { get; }

    /// <summary>
    /// The client's last-reported measured/estimated downlink, in kbps. Zero
    /// until the first report.
    /// </summary>
    int ObservedBandwidthKbps { get; }

    /// <summary>
    /// Records the client's download-buffer depth and observed downlink, and
    /// stamps the report time used by <see cref="HasFreshClientHealth"/>. Called
    /// from <c>LiveTranscodeHub.ReportBufferHealth</c> (SignalR) or the REST
    /// buffer-health endpoint — whichever the client uses.
    /// </summary>
    void ReportClientBufferHealth(TimeSpan bufferedAhead, int observedBandwidthKbps);

    /// <summary>
    /// True when a <see cref="ReportClientBufferHealth"/> report landed within
    /// <paramref name="maxAge"/> of now. An old client that never calls it (or a
    /// report older than the staleness window) makes this false, which is what
    /// keeps the network axis of the buffer-adaptive sweep skipped for that
    /// session — the pre-existing encoder-lead-only behavior applies unchanged.
    /// </summary>
    bool HasFreshClientHealth(TimeSpan maxAge);
}
