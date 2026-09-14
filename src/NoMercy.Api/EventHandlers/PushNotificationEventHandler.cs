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

using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Media;
using NoMercy.Events.Plugins;
using NoMercy.NmSystem.Auth;
using NoMercy.Notifications.Push;

namespace NoMercy.Api.EventHandlers;

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

    public PushNotificationEventHandler(
        IEventBus eventBus,
        IAuthTokenStore authTokenStore,
        NotificationSink notificationSink
    )
    {
        _authTokenStore = authTokenStore;
        _notificationSink = notificationSink;
        Track(eventBus.Subscribe<EncodingStartedEvent>(OnEncodingStarted));
        Track(eventBus.Subscribe<EncodingCompletedEvent>(OnEncodingCompleted));
        Track(eventBus.Subscribe<EncodingFailedEvent>(OnEncodingFailed));
        Track(eventBus.Subscribe<MediaAddedEvent>(OnMediaAdded));
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
    // host already understands.
    internal Task OnMediaAdded(MediaAddedEvent @event, CancellationToken _)
    {
        Notify(
            "media-added",
            new("New in your library", @event.Title, $"/{@event.MediaType}/{@event.MediaId}")
        );
        return Task.CompletedTask;
    }

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
