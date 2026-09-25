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
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Events.Plugins;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;

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
        IPlayableMediaProbe playableMediaProbe
    )
    {
        _authTokenStore = authTokenStore;
        _notificationSink = notificationSink;
        _playableMediaProbe = playableMediaProbe;
        Track(eventBus.Subscribe<EncodingStartedEvent>(OnEncodingStarted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompleted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompletedRecheckPending));
        Track(eventBus.Subscribe<EncodingFailedEvent>(OnEncodingFailed));
        Track(eventBus.Subscribe<MediaAddedEvent>(OnMediaAdded));
        Track(eventBus.Subscribe<MediaFilesScannedEvent>(OnMediaFilesScanned));
        Track(eventBus.Subscribe<LibraryScanCompletedEvent>(OnLibraryScanCompleted));
        Track(eventBus.Subscribe<PluginErrorOccurredEvent>(OnPluginError));
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

    internal Task OnLibraryScanCompleted(LibraryScanCompletedEvent @event, CancellationToken _)
    {
        Notify(
            "library-scan-complete",
            new(
                "Library scan finished",
                $"{@event.LibraryName} scanned, {@event.ItemsFound} item(s) found",
                "/libraries"
            )
        );
        return Task.CompletedTask;
    }

    internal Task OnPluginError(PluginErrorOccurredEvent @event, CancellationToken _)
    {
        Notify(
            "plugin-error",
            new($"{@event.PluginName} failed", @event.ErrorMessage, "/dashboard/plugins")
        );
        return Task.CompletedTask;
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
