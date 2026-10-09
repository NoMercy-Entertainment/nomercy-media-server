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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.LiveTranscode.Protocol;
using NoMercy.Storage;

namespace NoMercy.Encoder.LiveTranscode;

/// <summary>
/// Singleton that holds live transcode runtime state — maps a session id to
/// its <see cref="ILiveSession"/> plus a background task draining the async
/// segment stream into an indexed buffer so the HTTP layer can serve any
/// requested segment id without re-enumerating the source channel.
/// </summary>
public class LiveStreamingService(
    ILogger<LiveStreamingService> logger,
    IStorage storage,
    ILiveSegmentInventory segmentInventory,
    ILiveSessionTransport? transport = null,
    ISessionManager? sessionManager = null
) : ILiveStreamingService, IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<string, LiveRuntimeSession> _runtimes = new();

    // Tombstone of recently removed sessions so the API can distinguish an
    // expired/ended session (410 Gone) from one that never existed (404).
    private readonly ConcurrentDictionary<string, DateTime> _recentlyRemoved = new();
    private static readonly TimeSpan TombstoneWindow = TimeSpan.FromMinutes(30);

    public IReadOnlyCollection<string> ActiveSessionIds => _runtimes.Keys.ToList();

    public void Register(
        ILiveSession session,
        TimeSpan targetSegmentDuration,
        string? scratchDirectory = null,
        bool isAudioRenditionChild = false
    )
    {
        LiveRuntimeSession runtime = new(session, targetSegmentDuration, scratchDirectory)
        {
            IsAudioRenditionChild = isAudioRenditionChild,
        };
        if (!_runtimes.TryAdd(session.SessionId, runtime))
        {
            throw new InvalidOperationException(
                $"Live session '{session.SessionId}' is already registered"
            );
        }

        // Fires on a quality change only (see ILiveSession.AttachBufferResetCallback) —
        // the new encode's segments differ from the old quality's, so the stale
        // ones must be purged from disk too. Without this the coverage-aware
        // planner in LiveEncoder.SpawnRunner would see the old-quality files still
        // sitting on disk, treat that range as "already covered", and skip
        // re-encoding it — silently serving the previous quality forever.
        session.AttachBufferResetCallback(() =>
        {
            runtime.ResetBuffer();
            if (runtime.ScratchDirectory is { Length: > 0 } scratch)
                segmentInventory.Purge(scratch);
        });
        runtime.DrainerTask = Task.Run(() => DrainAsync(runtime));
        logger.LogDebug("Registered live session {SessionId}", session.SessionId);
    }

    public void StampChildAudioSessions(string sessionId, IReadOnlyList<string> childSessionIds)
    {
        if (_runtimes.TryGetValue(sessionId, out LiveRuntimeSession? runtime))
            runtime.ChildAudioSessionIds = childSessionIds;
    }

    public void StampRequestContext(
        string sessionId,
        MediaInfo mediaInfo,
        ClientCapabilities client
    )
    {
        if (_runtimes.TryGetValue(sessionId, out LiveRuntimeSession? runtime))
        {
            runtime.CachedMediaInfo = mediaInfo;
            runtime.ClientCapabilities = client;
        }
    }

    public void StampAudioRenditions(string sessionId, IReadOnlyList<LiveAudioRendition> renditions)
    {
        if (_runtimes.TryGetValue(sessionId, out LiveRuntimeSession? runtime))
            runtime.AudioRenditions = renditions;
    }

    public bool TryGetRuntime(string sessionId, out LiveRuntimeSession runtime)
    {
        return _runtimes.TryGetValue(sessionId, out runtime!);
    }

    public IReadOnlyList<LiveSessionSnapshot> GetActiveSessions()
    {
        List<LiveSessionSnapshot> snapshots = [];

        foreach (KeyValuePair<string, LiveRuntimeSession> kv in _runtimes)
        {
            ILiveSession session = kv.Value.Session;
            snapshots.Add(
                new(
                    SessionId: session.SessionId,
                    State: session.State,
                    QualityId: session.CurrentQuality.Id,
                    QualityLabel: session.CurrentQuality.Label,
                    Width: session.CurrentQuality.Width,
                    Height: session.CurrentQuality.Height,
                    BitrateKbps: session.CurrentQuality.BitrateKbps,
                    PositionSeconds: session.TranscodedPosition.TotalSeconds,
                    BufferAheadSeconds: session.BufferAhead.TotalSeconds,
                    SegmentCount: kv.Value.HighestSegmentIndex + 1,
                    IsComplete: kv.Value.IsComplete,
                    LastAccess: kv.Value.LastAccess
                )
            );
        }

        return snapshots;
    }

    public async Task RemoveAsync(string sessionId)
    {
        try
        {
            if (_runtimes.TryRemove(sessionId, out LiveRuntimeSession? runtime))
            {
                logger.LogDebug("Removing live session {SessionId}", sessionId);

                // Cascade to the per-language audio children so switching audio
                // never outlives the video session it belongs to.
                foreach (string childId in runtime.ChildAudioSessionIds)
                {
                    try
                    {
                        await RemoveAsync(childId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            ex,
                            "Could not remove audio child {ChildSessionId} of live session {SessionId}",
                            childId,
                            sessionId
                        );
                    }
                }

                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(runtime.ScratchDirectory))
                        TryDeleteScratch(runtime.ScratchDirectory, sessionId);
                }
            }
        }
        finally
        {
            // Atomic teardown: even if disposing the runtime threw, the session
            // is always pulled from the manager and tombstoned, so it can never
            // linger as a ghost and a later request gets 410 Gone.
            sessionManager?.RemoveSession(sessionId);
            RecordRemoved(sessionId);
        }
    }

    /// <summary>
    /// Host shutdown hook. The DI container disposes this singleton when the
    /// server stops; without it, every in-flight live session's ffmpeg child
    /// would keep running as an orphan (the session CTS is not linked to the
    /// application lifetime). Tear down each session via the normal RemoveAsync
    /// path so its process dies, audio children cascade, and scratch is cleaned.
    ///
    /// Both IAsyncDisposable and IDisposable are implemented: the Generic Host
    /// shutdown path disposes async, but a synchronously-disposed container
    /// throws on an async-only registration, so the sync fallback is required.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await DisposeAllSessionsAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        // Sync fallback for a synchronously-disposed container. Safe to block:
        // the teardown uses ConfigureAwait(false) throughout, so there is no
        // captured-context deadlock.
        DisposeAllSessionsAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task DisposeAllSessionsAsync()
    {
        // Snapshot the keys — RemoveAsync mutates _runtimes as it goes.
        foreach (string sessionId in _runtimes.Keys.ToList())
        {
            try
            {
                await RemoveAsync(sessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Could not remove live session {SessionId} during shutdown",
                    sessionId
                );
            }
        }
    }

    public bool WasRecentlyRemoved(string sessionId)
    {
        if (!_recentlyRemoved.TryGetValue(sessionId, out DateTime removedAt))
            return false;
        if (DateTime.UtcNow - removedAt > TombstoneWindow)
        {
            _recentlyRemoved.TryRemove(sessionId, out _);
            return false;
        }
        return true;
    }

    private void RecordRemoved(string sessionId)
    {
        _recentlyRemoved[sessionId] = DateTime.UtcNow;
        // Keep the tombstone set bounded by pruning expired entries.
        if (_recentlyRemoved.Count > 256)
        {
            DateTime cutoff = DateTime.UtcNow - TombstoneWindow;
            foreach (KeyValuePair<string, DateTime> kv in _recentlyRemoved)
            {
                if (kv.Value < cutoff)
                    _recentlyRemoved.TryRemove(kv.Key, out _);
            }
        }
    }

    private void TryDeleteScratch(string scratchDirectory, string sessionId)
    {
        try
        {
            if (storage.Exists(scratchDirectory))
            {
                storage.DeleteDirectory(scratchDirectory, recursive: true);
                logger.LogDebug(
                    "Deleted live session scratch {Dir} for {SessionId}",
                    scratchDirectory,
                    sessionId
                );
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a segment might still be held open by Windows until
            // the FFmpeg handle fully releases. The temp dir will be cleaned
            // up on the next server start or by the OS temp sweep.
            logger.LogWarning(
                ex,
                "Could not delete scratch {Dir} for live session {SessionId}",
                scratchDirectory,
                sessionId
            );
        }
    }

    private async Task DrainAsync(LiveRuntimeSession runtime)
    {
        try
        {
            await foreach (
                Segment segment in runtime.Session.Segments.WithCancellation(
                    runtime.DrainerCancellation
                )
            )
            {
                runtime.BufferSegment(segment);
                await PushSegmentReadyAsync(runtime, segment).ConfigureAwait(false);
            }

            runtime.MarkComplete();
            logger.LogDebug(
                "Drainer for {SessionId} completed — buffered {Count} segments",
                runtime.Session.SessionId,
                runtime.HighestSegmentIndex + 1
            );
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Drainer for live session {SessionId} failed",
                runtime.Session.SessionId
            );
            runtime.MarkComplete();
        }
    }

    private async Task PushSegmentReadyAsync(LiveRuntimeSession runtime, Segment segment)
    {
        if (transport is null)
            return;

        string sessionId = runtime.Session.SessionId;
        string relativeUrl =
            $"/api/v1/streaming/live/sessions/{sessionId}/segment/{runtime.CurrentEpoch}/{segment.Index}.ts";

        SegmentReadyMessage message = new(
            Index: segment.Index,
            StartTimeSeconds: segment.StartTime.TotalSeconds,
            DurationSeconds: segment.Duration.TotalSeconds,
            RelativeUrl: relativeUrl,
            SizeBytes: segment.SizeBytes
        );

        try
        {
            await transport
                .SendToClientAsync(sessionId, message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "Transport push failed for SegmentReady on session {SessionId}",
                sessionId
            );
        }
    }
}
