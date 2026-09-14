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

using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Playback;
using NoMercy.Networking.Messaging;

namespace NoMercy.Networking.Messaging.EventHandlers;

public class SignalRPlaybackEventHandler : EventSubscriber
{
    private readonly IClientMessenger _clientMessenger;

    private readonly ILogger<SignalRPlaybackEventHandler> _logger;

    public SignalRPlaybackEventHandler(
        ILogger<SignalRPlaybackEventHandler> logger,
        IEventBus eventBus,
        IClientMessenger clientMessenger
    )
    {
        _logger = logger;
        _clientMessenger = clientMessenger;
        Track(eventBus.Subscribe<PlaybackStartedEvent>(OnPlaybackStarted));
        Track(eventBus.Subscribe<PlaybackProgressUpdatedEvent>(OnPlaybackProgress));
        Track(eventBus.Subscribe<PlaybackCompletedEvent>(OnPlaybackCompleted));
    }

    internal async Task OnPlaybackStarted(PlaybackStartedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "PlaybackStarted",
            "dashboardHub",
            new
            {
                @event.UserId,
                @event.MediaId,
                @event.MediaIdentifier,
                @event.MediaType,
                @event.DeviceId,
                @event.Timestamp,
            }
        );

        _logger.LogInformation(
            "Playback started: User={UserId}, Media={MediaId}, Type={MediaType}",
            [@event.UserId, @event.MediaId, @event.MediaType]
        );
    }

    internal async Task OnPlaybackProgress(
        PlaybackProgressUpdatedEvent @event,
        CancellationToken ct
    )
    {
        // Progress events are high-frequency; broadcast but don't log to avoid noise
        await _clientMessenger.SendToAll(
            "PlaybackProgress",
            "dashboardHub",
            new
            {
                @event.UserId,
                @event.MediaId,
                @event.MediaIdentifier,
                @event.Position,
                @event.Duration,
            }
        );
    }

    internal async Task OnPlaybackCompleted(PlaybackCompletedEvent @event, CancellationToken ct)
    {
        await _clientMessenger.SendToAll(
            "PlaybackCompleted",
            "dashboardHub",
            new
            {
                @event.UserId,
                @event.MediaId,
                @event.MediaIdentifier,
                @event.MediaType,
                @event.Timestamp,
            }
        );

        _logger.LogInformation(
            "Playback completed: User={UserId}, Media={MediaId}, Type={MediaType}",
            [@event.UserId, @event.MediaId, @event.MediaType]
        );
    }
}
