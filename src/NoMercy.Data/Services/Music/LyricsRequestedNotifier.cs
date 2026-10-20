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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Services.Music;

/// <summary>
/// Raises <see cref="PluginTopics.LyricsRequested" /> when a track's lyrics
/// were served missing or without timing, at most once per track per day: a
/// plugin that generates timing needs one cue, not one per play, and a track
/// nobody can find lyrics for must not become a daily sweep on its own.
/// A failed publish is logged and never fails the request that raised it.
/// </summary>
public class LyricsRequestedNotifier(
    IEventBus eventBus,
    ILogger<LyricsRequestedNotifier>? logger = null
)
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, DateTime> _notifiedAt = new();
    private readonly TimeSpan _window = DefaultWindow;
    private readonly ILogger<LyricsRequestedNotifier> _logger =
        logger ?? NullLogger<LyricsRequestedNotifier>.Instance;

    // Test-only seam (NoMercy.Tests.Repositories has InternalsVisibleTo) so a
    // test can shrink the window instead of waiting out a day.
    internal LyricsRequestedNotifier(IEventBus eventBus, TimeSpan window)
        : this(eventBus)
    {
        _window = window;
    }

    /// <summary>
    /// Publishes the topic unless this track was already announced inside the
    /// window. Returns whether it published.
    /// </summary>
    public async Task<bool> NotifyAsync(Guid trackId, CancellationToken ct = default)
    {
        DateTime now = DateTime.UtcNow;
        bool claimed = false;
        _notifiedAt.AddOrUpdate(
            trackId,
            _ =>
            {
                claimed = true;
                return now;
            },
            (_, last) =>
            {
                claimed = now - last >= _window;
                return claimed ? now : last;
            }
        );
        if (!claimed)
            return false;

        try
        {
            // Sender Ulid.Empty: raised by the host, so the in-process events
            // facade delivers it to every plugin subscribed to the topic.
            await eventBus.PublishAsync(
                PluginMessageEvent.From(
                    Ulid.Empty,
                    PluginTopics.LyricsRequested,
                    new PluginLyricsRequested(trackId, HasSyncedTiming: false)
                ),
                ct
            );
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Publishing lyrics requested for track {TrackId} failed",
                trackId
            );
            return false;
        }
    }
}
