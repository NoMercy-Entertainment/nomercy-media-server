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

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.LiveTranscode.Protocol;

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// Background service that evaluates buffer health for every active live session
/// every 5 seconds and acts on the result. Two independent axes are evaluated:
/// a NETWORK axis (the client's reported download-buffer depth and observed
/// downlink — <see cref="ILiveSession.ClientBufferedAhead"/> /
/// <see cref="ILiveSession.ObservedBandwidthKbps"/>) drives emergency drops,
/// bandwidth-fit drops, and hysteresis-gated raises; an ENCODER-CAPACITY axis
/// (<see cref="ILiveSession.BufferAhead"/> — how far the transcoder ran ahead
/// of the playhead) drives suspend/resume and a capacity-limited drop. The
/// network axis is evaluated first and, when it acts, takes precedence over
/// the encoder axis for that sweep — a network stall must re-encode low from
/// the playhead, not just leave a racing encoder suspended. A session with no
/// fresh client-health report (an old client that never reports, or a stale
/// report) skips the network axis entirely, so it gets exactly today's
/// encoder-lead-only behavior. Runs independently from the 30-second idle
/// reaper so adaptive reactions are fast enough to matter.
/// </summary>
public class BufferAdaptiveService(
    ILiveStreamingService streamingService,
    ILiveQualitySelector qualitySelector,
    BufferManager bufferManager,
    SpeedIndex speedIndex,
    IResourceBudget resourceBudget,
    LiveSessionLimits limits,
    ILogger<BufferAdaptiveService> logger,
    ILiveSessionTransport? transport = null
) : BackgroundService
{
    private static readonly TimeSpan EvalInterval = TimeSpan.FromSeconds(5);

    // Consecutive raise-eligible sweep count per session, keyed by SessionId.
    // Pruned every sweep to whatever sessions are still active so a torn-down
    // session's counter never leaks.
    private readonly ConcurrentDictionary<string, int> _raiseSweepCounts = new();

    // A runner that just (re)started needs a few seconds to write its first
    // segment; until then its buffer is legitimately near-zero. Acting inside
    // this window would misread that as "drop quality" and cancel the fresh
    // runner every sweep, so a seek / quality-change / resume never completes.
    private static readonly TimeSpan TranscodeWarmup = TimeSpan.FromSeconds(10);

    // FFmpeg speed (× realtime) above which the encoder is comfortably keeping up.
    // A low client buffer while the encoder runs this fast just means the viewer
    // is near the production frontier (normal right after a seek), not that the
    // hardware can't sustain the resolution — so quality must not be dropped.
    private const double QualityKeepUpSpeed = 1.2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogDebug("BufferAdaptiveService started (eval interval = 5 s)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(EvalInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await EvaluateAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "BufferAdaptiveService evaluation sweep failed; will retry");
            }
        }
    }

    internal async Task EvaluateAllAsync(CancellationToken ct)
    {
        IReadOnlyCollection<string> activeSessionIds = streamingService.ActiveSessionIds;
        PruneRaiseSweepCounts(activeSessionIds);

        foreach (string sessionId in activeSessionIds)
        {
            if (!streamingService.TryGetRuntime(sessionId, out LiveRuntimeSession runtime))
                continue;

            if (runtime.IsComplete)
                continue;

            try
            {
                await EvaluateSessionAsync(runtime, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One session whose runner misbehaves must not starve the sessions
                // after it of their suspend, resume and quality drops.
                logger.LogWarning(
                    ex,
                    "BufferAdaptive: evaluation failed for session {SessionId}; skipping it this sweep",
                    sessionId
                );
            }
        }
    }

    private async Task EvaluateSessionAsync(LiveRuntimeSession runtime, CancellationToken ct)
    {
        ILiveSession session = runtime.Session;

        bool networkActionTaken = await TryApplyNetworkAxisAsync(runtime, session, ct)
            .ConfigureAwait(false);

        if (!networkActionTaken)
            await ApplyEncoderCapacityAxisAsync(runtime, session, ct).ConfigureAwait(false);

        await PushTranscodeStateAsync(session, ct).ConfigureAwait(false);
    }

    private async Task ApplyEncoderCapacityAxisAsync(
        LiveRuntimeSession runtime,
        ILiveSession session,
        CancellationToken ct
    )
    {
        bool isSuspended = session.State == LiveSessionState.Buffered;
        BufferAction action = bufferManager.Evaluate(session.BufferAhead, isSuspended);

        // Suppress adaptive actions during the warm-up window after a
        // (re)start so the fresh runner is left alone to fill its buffer; the
        // client's own request pacing drives production in the meantime. State
        // is still reported by the caller regardless.
        if (DateTime.UtcNow - session.LastTranscodeStart < TranscodeWarmup)
            action = BufferAction.None;

        // Keep resolution up while the encoder is out-running realtime: a thin
        // buffer then reflects where the viewer is, not a struggling GPU.
        // Suspend/Resume still apply — only the quality drops are gated.
        if (
            action is BufferAction.DropQuality or BufferAction.EmergencyDropQuality
            && session.CurrentSpeed >= QualityKeepUpSpeed
        )
            action = BufferAction.None;

        switch (action)
        {
            case BufferAction.Suspend:
                logger.LogDebug(
                    "BufferAdaptive: plan=encoder-suspend session={SessionId} buffer={Buf:F1}s",
                    session.SessionId,
                    session.BufferAhead.TotalSeconds
                );
                session.Suspend();
                break;

            case BufferAction.Resume:
                logger.LogDebug(
                    "BufferAdaptive: plan=encoder-resume session={SessionId} buffer={Buf:F1}s",
                    session.SessionId,
                    session.BufferAhead.TotalSeconds
                );
                session.Resume();
                break;

            case BufferAction.DropQuality:
                await TryDropQualityAsync(runtime, session, QualityChangeReason.AutoAdaptive, ct)
                    .ConfigureAwait(false);
                break;

            case BufferAction.EmergencyDropQuality:
                await TryDropQualityAsync(
                        runtime,
                        session,
                        QualityChangeReason.AutoAdaptive,
                        ct,
                        emergency: true
                    )
                    .ConfigureAwait(false);
                break;

            case BufferAction.None:
                break;
        }
    }

    /// <summary>
    /// Evaluates the network axis for one session: emergency drop on a
    /// near-stall client buffer, bandwidth-fit drop when the downlink no
    /// longer sustains the current tier, or a hysteresis-gated one-tier raise
    /// when it comfortably sustains more. Returns true when an action was
    /// applied, so the caller skips the encoder-capacity axis for this sweep —
    /// a network drop/emergency must take precedence over an encoder-lead
    /// Suspend. Returns false (network axis skipped or nothing to do) when
    /// there is no fresh client-health report, session context is missing, or
    /// the current tier already fits.
    /// </summary>
    private async Task<bool> TryApplyNetworkAxisAsync(
        LiveRuntimeSession runtime,
        ILiveSession session,
        CancellationToken ct
    )
    {
        BufferThresholds thresholds = limits.Buffer;

        if (
            !session.HasFreshClientHealth(
                TimeSpan.FromSeconds(thresholds.ClientHealthStalenessSeconds)
            )
        )
            return false;

        if (runtime.CachedMediaInfo is null || runtime.ClientCapabilities is null)
            return false;

        LiveQuality[] available = qualitySelector.GetAvailableQualities(
            runtime.CachedMediaInfo,
            runtime.ClientCapabilities,
            speedIndex,
            resourceBudget
        );
        if (available.Length == 0)
            return false;

        LiveQuality current = session.CurrentQuality;
        int currentIndex = Array.FindIndex(available, q => q.Id == current.Id);
        if (currentIndex < 0)
            currentIndex = 0;

        TimeSpan clientBuffer = session.ClientBufferedAhead;

        if (clientBuffer.TotalSeconds < thresholds.ClientEmergencyStallSeconds)
        {
            ResetRaiseSweepCount(session.SessionId);

            LiveQuality lowest = available[^1];
            if (lowest.Id == current.Id)
                return false;

            logger.LogInformation(
                "BufferAdaptive: plan=network-emergency-drop session={SessionId} {From}→{To} clientBuffer={Buf:F1}s",
                session.SessionId,
                current.Label,
                lowest.Label,
                clientBuffer.TotalSeconds
            );
            await ChangeQualityAndPushAsync(session, lowest, QualityChangeReason.AutoAdaptive, ct)
                .ConfigureAwait(false);
            return true;
        }

        int observedBandwidthKbps = session.ObservedBandwidthKbps;
        LiveQuality fit = qualitySelector.SelectForBandwidth(
            available,
            observedBandwidthKbps,
            thresholds.UsableBandwidthFraction,
            current
        );
        int fitIndex = Array.FindIndex(available, q => q.Id == fit.Id);
        if (fitIndex < 0)
            fitIndex = currentIndex;

        // Higher index = lower resolution tier (available is ordered high→low),
        // so a fit index below the current tier means the downlink no longer
        // sustains it.
        if (fitIndex > currentIndex)
        {
            ResetRaiseSweepCount(session.SessionId);

            logger.LogInformation(
                "BufferAdaptive: plan=network-drop session={SessionId} {From}→{To} bandwidth={Kbps}kbps",
                session.SessionId,
                current.Label,
                fit.Label,
                observedBandwidthKbps
            );
            await ChangeQualityAndPushAsync(session, fit, QualityChangeReason.AutoAdaptive, ct)
                .ConfigureAwait(false);
            return true;
        }

        bool bandwidthSustainsHigher = fitIndex < currentIndex;
        bool bufferHealthy = clientBuffer.TotalSeconds > thresholds.RaiseHealthyBufferSeconds;

        if (!bandwidthSustainsHigher || !bufferHealthy)
        {
            ResetRaiseSweepCount(session.SessionId);
            return false;
        }

        int sweepCount = _raiseSweepCounts.AddOrUpdate(
            session.SessionId,
            1,
            (_, previous) => previous + 1
        );

        if (sweepCount < thresholds.RaiseSustainSweeps)
        {
            logger.LogDebug(
                "BufferAdaptive: plan=network-raise-pending session={SessionId} sweep={Count}/{Sustain}",
                session.SessionId,
                sweepCount,
                thresholds.RaiseSustainSweeps
            );
            return false;
        }

        ResetRaiseSweepCount(session.SessionId);

        int raiseIndex = Math.Max(0, currentIndex - 1);
        if (raiseIndex == currentIndex)
            return false;

        LiveQuality raiseTarget = available[raiseIndex];
        logger.LogInformation(
            "BufferAdaptive: plan=network-raise session={SessionId} {From}→{To} bandwidth={Kbps}kbps clientBuffer={Buf:F1}s",
            session.SessionId,
            current.Label,
            raiseTarget.Label,
            observedBandwidthKbps,
            clientBuffer.TotalSeconds
        );
        await ChangeQualityAndPushAsync(session, raiseTarget, QualityChangeReason.AutoAdaptive, ct)
            .ConfigureAwait(false);
        return true;
    }

    private void ResetRaiseSweepCount(string sessionId) =>
        _raiseSweepCounts.TryRemove(sessionId, out _);

    private void PruneRaiseSweepCounts(IReadOnlyCollection<string> activeSessionIds)
    {
        if (_raiseSweepCounts.IsEmpty)
            return;

        foreach (string trackedSessionId in _raiseSweepCounts.Keys)
        {
            if (!activeSessionIds.Contains(trackedSessionId))
                _raiseSweepCounts.TryRemove(trackedSessionId, out _);
        }
    }

    private async Task TryDropQualityAsync(
        LiveRuntimeSession runtime,
        ILiveSession session,
        QualityChangeReason reason,
        CancellationToken ct,
        bool emergency = false
    )
    {
        if (runtime.CachedMediaInfo is null || runtime.ClientCapabilities is null)
            return;

        LiveQuality[] available = qualitySelector.GetAvailableQualities(
            runtime.CachedMediaInfo,
            runtime.ClientCapabilities,
            speedIndex,
            resourceBudget
        );

        if (available.Length == 0)
            return;

        LiveQuality current = session.CurrentQuality;

        LiveQuality? target;
        if (emergency)
        {
            target = available[^1];
        }
        else
        {
            int currentIndex = Array.FindIndex(available, q => q.Id == current.Id);
            int nextIndex = currentIndex >= 0 ? currentIndex + 1 : available.Length - 1;
            target = nextIndex < available.Length ? available[nextIndex] : null;
        }

        if (target is null || target.Id == current.Id)
            return;

        logger.LogInformation(
            "BufferAdaptive: plan=encoder-drop-quality session={SessionId} {From}→{To} (reason={Reason}, buffer={Buf:F1}s)",
            session.SessionId,
            current.Label,
            target.Label,
            reason,
            session.BufferAhead.TotalSeconds
        );

        await ChangeQualityAndPushAsync(session, target, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a quality change and pushes the <see cref="QualityChangedMessage"/>
    /// notification, shared by both the encoder-capacity and network axes. A
    /// no-op when <paramref name="target"/> already matches the session's
    /// current quality.
    /// </summary>
    private async Task ChangeQualityAndPushAsync(
        ILiveSession session,
        LiveQuality target,
        QualityChangeReason reason,
        CancellationToken ct
    )
    {
        if (target.Id == session.CurrentQuality.Id)
            return;

        await session.ChangeQualityAsync(target.Id, target, ct).ConfigureAwait(false);
        await PushQualityChangedAsync(session, target, reason, ct).ConfigureAwait(false);
    }

    private async Task PushQualityChangedAsync(
        ILiveSession session,
        LiveQuality newQuality,
        QualityChangeReason reason,
        CancellationToken ct
    )
    {
        if (transport is null)
            return;

        QualityChangedMessage message = new(NewQuality: newQuality, Reason: reason);

        try
        {
            await transport.SendToClientAsync(session.SessionId, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for QualityChanged on session {SessionId}",
                session.SessionId
            );
        }
    }

    private async Task PushTranscodeStateAsync(ILiveSession session, CancellationToken ct)
    {
        if (transport is null)
            return;

        TranscodeStateMessage message = new(
            Speed: session.CurrentSpeed,
            BufferAheadSeconds: session.BufferAhead.TotalSeconds,
            State: session.State
        );

        try
        {
            await transport.SendToClientAsync(session.SessionId, message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for TranscodeState on session {SessionId}",
                session.SessionId
            );
        }
    }
}
