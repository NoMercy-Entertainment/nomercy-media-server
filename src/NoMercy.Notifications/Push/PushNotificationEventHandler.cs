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
using NoMercy.Authorization;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Events.Playback;
using NoMercy.Events.Plugins;
using NoMercy.NmSystem.Auth;

namespace NoMercy.Notifications.Push;

/// <summary>
/// Only events that describe something a person asked to be told about belong
/// here. LibraryRefreshedEvent does not: it is a cache-invalidation signal
/// carrying a QueryKey, published from dozens of sites and several times per
/// single user action, including every continue-watching edit.
/// </summary>
public class PushNotificationEventHandler : EventSubscriber
{
    private const string UserNotificationChannel = "user-notification";

    private readonly IAuthTokenStore _authTokenStore;
    private readonly NotificationSink _notificationSink;
    private readonly IPlayableMediaProbe _playableMediaProbe;
    private readonly TimeProvider _timeProvider;
    private readonly IUserCache? _userCache;
    private readonly object _playbackToolsNotificationLock = new();
    private DateTimeOffset? _lastPlaybackToolsFailureNotification;
    private bool _playbackToolsRecoveryNotified;
    private readonly List<(
        string Channel,
        PushPayload Payload
    )> _pendingPlaybackToolsNotifications = [];
    private bool _waitingForOwners;

    // How often, and for how long, the handler waits for an owner or manager to
    // appear in the user cache before it gives up. The queued notifications stay
    // queued: the next playback-tools event delivers them.
    internal TimeSpan OwnerWaitInterval { get; init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan OwnerWaitLimit { get; init; } = TimeSpan.FromHours(24);

    private static readonly TimeSpan ScanTallyLifetime = TimeSpan.FromHours(24);

    // The scan push says how many titles the scan added, so it waits for the
    // import jobs the scan queued. Keyed by library: import jobs carry no scan
    // id, because the queue drops a payload it already holds and a scan id would
    // make every rescan queue its imports again.
    private readonly object _scanLock = new();
    private readonly Dictionary<Ulid, ScanTally> _scans = new();

    // MediaImportJobs publish MediaAddedEvent as soon as metadata lands, well
    // before FileRescanJob has matched a single file on disk. Pushing then
    // tells the user there is something to watch when there is nothing to
    // play yet, so the push waits here for MediaFilesScannedEvent to confirm a
    // playable VideoFile exists. Keyed by (MediaType, MediaId) because a movie
    // and a show can share the same TMDB id.
    private readonly ConcurrentDictionary<
        (string MediaType, int MediaId),
        MediaAddedEvent
    > _pendingMediaAdded = new();

    public PushNotificationEventHandler(
        IEventBus eventBus,
        IAuthTokenStore authTokenStore,
        NotificationSink notificationSink,
        IPlayableMediaProbe playableMediaProbe,
        TimeProvider? timeProvider = null,
        IUserCache? userCache = null
    )
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _authTokenStore = authTokenStore;
        _notificationSink = notificationSink;
        _playableMediaProbe = playableMediaProbe;
        _userCache = userCache;
        Track(eventBus.Subscribe<EncodingStartedEvent>(OnEncodingStarted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompleted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompletedRecheckPending));
        Track(eventBus.Subscribe<EncodingFailedEvent>(OnEncodingFailed));
        Track(eventBus.Subscribe<MediaAddedEvent>(OnMediaAdded));
        Track(eventBus.Subscribe<MediaFilesScannedEvent>(OnMediaFilesScanned));
        Track(eventBus.Subscribe<LibraryScanStartedEvent>(OnLibraryScanStarted));
        Track(eventBus.Subscribe<LibraryImportsQueuedEvent>(OnLibraryImportsQueued));
        Track(eventBus.Subscribe<LibraryScanCompletedEvent>(OnLibraryScanCompleted));
        Track(eventBus.Subscribe<MediaImportFinishedEvent>(OnMediaImportFinished));
        Track(eventBus.Subscribe<PluginErrorOccurredEvent>(OnPluginError));
        Track(eventBus.Subscribe<PlaybackToolsDownloadFailedEvent>(OnPlaybackToolsDownloadFailed));
        Track(eventBus.Subscribe<PlaybackToolsReadyEvent>(OnPlaybackToolsReady));
        Track(eventBus.Subscribe<UserNotifiedEvent>(OnUserNotified));
    }

    internal Task OnEncodingStarted(EncodingStartedEvent @event, CancellationToken _)
    {
        Notify(
            "encode-started",
            new(
                "Encoding started",
                $"{Path.GetFileName(@event.InputPath)} started encoding with {@event.ProfileName}",
                null
            )
        );
        return Task.CompletedTask;
    }

    internal Task OnEncodingCompleted(EncodingCompletedEvent @event, CancellationToken _)
    {
        Notify(
            "encode-finished",
            new(
                "Encoding finished",
                $"{Path.GetFileName(@event.OutputPath)} finished encoding",
                null,
                Image: PushArtworkUrl.Build(@event.BackdropPath, PushArtworkUrl.BackdropWidth),
                Icon: PushArtworkUrl.Build(@event.PosterPath, PushArtworkUrl.PosterWidth)
            )
        );
        return Task.CompletedTask;
    }

    internal Task OnEncodingFailed(EncodingFailedEvent @event, CancellationToken _)
    {
        Notify(
            "encode-failed",
            new(
                "Encoding failed",
                @event.ErrorMessage,
                null,
                Image: PushArtworkUrl.Build(@event.BackdropPath, PushArtworkUrl.BackdropWidth),
                Icon: PushArtworkUrl.Build(@event.PosterPath, PushArtworkUrl.PosterWidth)
            )
        );
        return Task.CompletedTask;
    }

    // The one channel here that is about content rather than operations, so it
    // routes to the item itself: "/movie/123" is the shape every client's nav
    // host already understands. Pushing does not happen here: the item has no
    // playable file yet at import time, so this only records the pending
    // notification. OnMediaFilesScanned pushes once a file actually lands,
    // even if one was already there when this fired.
    internal Task OnMediaAdded(MediaAddedEvent @event, CancellationToken _)
    {
        _pendingMediaAdded[(@event.MediaType, @event.MediaId)] = @event;
        return Task.CompletedTask;
    }

    internal Task OnMediaFilesScanned(MediaFilesScannedEvent @event, CancellationToken ct) =>
        RecheckPendingAsync(
            _pendingMediaAdded.Keys.Where(key => key.MediaId == @event.MediaId),
            ct
        );

    // FileRescanJob only runs from an import or a manual rescan — a title
    // whose files land later via encoding (added, then encoded from a source
    // with nothing playable yet) never gets a MediaFilesScannedEvent, so it
    // would stay pending forever. EncodingCompletedEvent carries only JobId
    // (the movie/episode id the encode ran for) and no MediaType, so it
    // cannot be matched to a pending key the way MediaFilesScannedEvent can —
    // every pending entry is rechecked instead. The pending set only holds
    // titles between import and their first playable file, so it stays small.
    // VideoEncodeJob always awaits ScanEncodedOutputWithRetryAsync (which
    // writes the VideoFile row) before publishing this event, on every path
    // that reaches it (coordinator finalize, inline, and the OCR top-up).
    internal Task OnEncodingCompletedRecheckPending(
        EncodingCompletedEvent @event,
        CancellationToken ct
    ) => RecheckPendingAsync(_pendingMediaAdded.Keys.ToList(), ct);

    private async Task RecheckPendingAsync(
        IEnumerable<(string MediaType, int MediaId)> keys,
        CancellationToken ct
    )
    {
        foreach ((string MediaType, int MediaId) key in keys.ToList())
        {
            if (!_pendingMediaAdded.TryGetValue(key, out MediaAddedEvent? pending))
                continue;

            bool hasPlayableVideo = await _playableMediaProbe.HasPlayableVideoAsync(
                key.MediaType,
                key.MediaId,
                ct
            );
            if (!hasPlayableVideo)
                continue;

            if (!_pendingMediaAdded.TryRemove(key, out _))
                continue;

            Notify(
                "media-added",
                new("New in your library", pending.Title, BuildMediaRoute(pending))
            );
        }
    }

    // MediaAddedEvent.MediaType is "tvshow" — the value MediaAddedEvent /
    // SignalR subscribers already key off — but no client route is nested
    // under /tvshow; the web/KMP nav host uses /tv/<id>.
    private static string BuildMediaRoute(MediaAddedEvent @event) =>
        @event.MediaType switch
        {
            "tvshow" => $"/tv/{@event.MediaId}",
            _ => $"/{@event.MediaType}/{@event.MediaId}",
        };

    internal Task OnLibraryScanStarted(LibraryScanStartedEvent @event, CancellationToken _)
    {
        lock (_scanLock)
        {
            Sweep();
            ScanTally tally = TallyFor(@event.LibraryId, @event.LibraryName);
            tally.ScanEnded = false;
        }

        return Task.CompletedTask;
    }

    internal Task OnLibraryImportsQueued(LibraryImportsQueuedEvent @event, CancellationToken _)
    {
        lock (_scanLock)
        {
            Sweep();
            ScanTally tally = TallyFor(@event.LibraryId, @event.LibraryName);
            tally.Outstanding += @event.Count;
        }

        return Task.CompletedTask;
    }

    internal Task OnLibraryScanCompleted(LibraryScanCompletedEvent @event, CancellationToken _)
    {
        lock (_scanLock)
        {
            Sweep();
            if (!_scans.TryGetValue(@event.LibraryId, out ScanTally? tally))
                return Task.CompletedTask;

            tally.Touch(_timeProvider);
            tally.ScanEnded = true;
            NotifyWhenScanSettled(@event.LibraryId, tally);
        }

        return Task.CompletedTask;
    }

    // An import that finishes while no scan of its library is open belongs to
    // nobody this handler is waiting on (queued before an upgrade, or by a
    // controller), so it is ignored rather than counted against a later scan.
    internal Task OnMediaImportFinished(MediaImportFinishedEvent @event, CancellationToken _)
    {
        lock (_scanLock)
        {
            Sweep();
            if (!_scans.TryGetValue(@event.LibraryId, out ScanTally? tally))
                return Task.CompletedTask;

            tally.Touch(_timeProvider);
            tally.Outstanding--;
            tally.Added += @event.Added;
            tally.Failed += @event.Failed;
            NotifyWhenScanSettled(@event.LibraryId, tally);
        }

        return Task.CompletedTask;
    }

    internal int PendingScanCount
    {
        get
        {
            lock (_scanLock)
            {
                return _scans.Count;
            }
        }
    }

    private ScanTally TallyFor(Ulid libraryId, string libraryName)
    {
        if (!_scans.TryGetValue(libraryId, out ScanTally? tally))
        {
            tally = new();
            _scans[libraryId] = tally;
        }

        tally.LibraryName = libraryName;
        tally.Touch(_timeProvider);
        return tally;
    }

    // Outstanding goes below zero while the scan is still walking its folders:
    // a job dispatched early can finish before the queued count is published.
    private void NotifyWhenScanSettled(Ulid libraryId, ScanTally tally)
    {
        if (!tally.ScanEnded || tally.Outstanding > 0)
            return;

        _scans.Remove(libraryId);
        string failed = tally.Failed > 0 ? $", {tally.Failed} failed" : string.Empty;
        Notify(
            "library-scan-complete",
            new(
                "Library scan finished",
                $"{tally.LibraryName} scanned, {tally.Added} title(s) added{failed}",
                "/libraries"
            )
        );
    }

    // A scan whose imports never report (a crashed worker, a deleted library)
    // would otherwise sit here until restart and absorb the next scan's counts.
    private void Sweep()
    {
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - ScanTallyLifetime;
        foreach (
            Ulid libraryId in _scans
                .Where(scan => scan.Value.LastTouched < cutoff)
                .Select(scan => scan.Key)
                .ToList()
        )
            _scans.Remove(libraryId);
    }

    private sealed class ScanTally
    {
        public string LibraryName { get; set; } = string.Empty;
        public int Outstanding { get; set; }
        public int Added { get; set; }
        public int Failed { get; set; }
        public bool ScanEnded { get; set; }
        public DateTimeOffset LastTouched { get; private set; }

        public void Touch(TimeProvider timeProvider) => LastTouched = timeProvider.GetUtcNow();
    }

    internal Task OnPluginError(PluginErrorOccurredEvent @event, CancellationToken _)
    {
        Notify(
            "plugin-error",
            new($"{@event.PluginName} failed", @event.ErrorMessage, "/dashboard/plugins")
        );
        return Task.CompletedTask;
    }

    internal Task OnPlaybackToolsDownloadFailed(
        PlaybackToolsDownloadFailedEvent @event,
        CancellationToken _
    )
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_playbackToolsNotificationLock)
        {
            if (_playbackToolsRecoveryNotified)
            {
                _lastPlaybackToolsFailureNotification = null;
                _playbackToolsRecoveryNotified = false;
            }

            if (
                _lastPlaybackToolsFailureNotification is { } last
                && now - last < TimeSpan.FromMinutes(30)
            )
                return Task.CompletedTask;

            _lastPlaybackToolsFailureNotification = now;
        }

        int minutes = Math.Max(1, (int)Math.Ceiling((@event.NextRetryAtUtc - now).TotalMinutes));
        string interval = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        NotifyPlaybackToolsOwners(
            "playback-tools-download-failed",
            new(
                "Playback tools failed to download",
                $"{@event.ErrorMessage}. Retrying in {interval}.",
                "/dashboard"
            )
        );
        return Task.CompletedTask;
    }

    internal Task OnPlaybackToolsReady(PlaybackToolsReadyEvent @event, CancellationToken _)
    {
        lock (_playbackToolsNotificationLock)
        {
            if (_playbackToolsRecoveryNotified)
                return Task.CompletedTask;

            _playbackToolsRecoveryNotified = true;
        }

        NotifyPlaybackToolsOwners(
            "playback-tools-ready",
            new(
                "Playback tools are ready",
                "Playback tools downloaded successfully. Library scans can continue.",
                "/dashboard"
            )
        );
        return Task.CompletedTask;
    }

    private void NotifyPlaybackToolsOwners(string channel, PushPayload payload)
    {
        if (_userCache is null)
            return;

        lock (_playbackToolsNotificationLock)
        {
            // A second queued failure is dropped, but the wait for an owner
            // still restarts below in case an earlier wait gave up.
            bool failureAlreadyQueued =
                channel == "playback-tools-download-failed"
                && _pendingPlaybackToolsNotifications.Any(pending => pending.Channel == channel);
            if (!failureAlreadyQueued)
                _pendingPlaybackToolsNotifications.Add((channel, payload));
            if (TryFlushPlaybackToolsNotifications())
                return;

            // InitRemaining can publish before SeedAuthData initializes UserCache.
            // Keep the first failure until the owner is available instead of
            // consuming the 30-minute throttle window without delivery.
            if (_waitingForOwners)
                return;

            _waitingForOwners = true;
        }

        _ = Task.Run(WaitForOwnersAsync);
    }

    private async Task WaitForOwnersAsync()
    {
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + OwnerWaitLimit;
        while (true)
        {
            await Task.Delay(OwnerWaitInterval);
            lock (_playbackToolsNotificationLock)
            {
                // On giving up the notifications stay queued; the next
                // playback-tools event starts a new wait and flushes them.
                if (TryFlushPlaybackToolsNotifications() || DateTimeOffset.UtcNow >= giveUpAt)
                {
                    _waitingForOwners = false;
                    return;
                }
            }
        }
    }

    // Called under _playbackToolsNotificationLock so queued failure and recovery
    // notifications are delivered in publish order when the user cache appears.
    private bool TryFlushPlaybackToolsNotifications()
    {
        if (_userCache is null)
            return true;

        List<Guid> userIds = _userCache
            .Users.Where(user => user.Owner || user.Manage)
            .Select(user => user.Id)
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
            return false;

        foreach (
            (string Channel, PushPayload Payload) pending in _pendingPlaybackToolsNotifications
        )
        foreach (Guid userId in userIds)
            _notificationSink.NotifyUser(
                userId,
                "videoHub",
                pending.Channel,
                pending.Payload,
                _authTokenStore.AccessToken ?? string.Empty
            );

        _pendingPlaybackToolsNotifications.Clear();
        return true;
    }

    // The access token gates push, not the whole notification: an unregistered
    // server still has to reach its own users over SignalR.
    internal Task OnUserNotified(UserNotifiedEvent @event, CancellationToken _)
    {
        if (@event.UserId is not { } userId)
            return Task.CompletedTask;

        _notificationSink.NotifyUser(
            userId,
            @event.Hub,
            UserNotificationChannel,
            new(
                @event.Title,
                @event.Message,
                @event.Route,
                @event.Type,
                Image: PushArtworkUrl.Build(@event.BackdropPath, PushArtworkUrl.BackdropWidth),
                Icon: PushArtworkUrl.Build(@event.PosterPath, PushArtworkUrl.PosterWidth)
            ),
            _authTokenStore.AccessToken ?? string.Empty
        );

        return Task.CompletedTask;
    }

    private void Notify(string channel, PushPayload payload)
    {
        string? accessToken = _authTokenStore.AccessToken;
        if (accessToken is null)
            return;

        _notificationSink.Notify(channel, payload, accessToken);
    }
}
