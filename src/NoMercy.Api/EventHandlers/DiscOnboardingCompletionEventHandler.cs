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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.TvShows;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.Events.Onboarding;
using NoMercy.OpticalMedia.Onboarding;

namespace NoMercy.Api.EventHandlers;

/// <summary>
/// Bridges the disc-onboarding subsystem to the existing "a file just got
/// imported" signal: <see cref="MediaFilesScannedEvent"/>, published by
/// <c>FileRescanJob</c> once a movie/episode's video files have been scanned
/// and written to the database — the same event <c>AutoEncodeSubscriber</c>
/// already reacts to. When it fires for the library a disc-onboarding session
/// confirmed against, and the resulting movie/show matches that session's
/// confirmed TMDB id, the session transitions to
/// <see cref="DiscOnboardingState.Complete"/> carrying the real library item
/// id/type, and rebroadcasts <see cref="DiscOnboardingStateChangedEvent"/> —
/// same "DiscOnboardingState" payload on <c>ripperHub</c>/<c>drivesHub"</c>
/// <see cref="DiscOnboardingEventHandler"/> already relays.
///
/// A rip whose import never resolves to a match (wrong candidate, the user's
/// own file organization interfered, etc.) simply stays in
/// <see cref="DiscOnboardingState.Ripping"/> forever — no timeout mechanism.
/// That matches today's behavior before this handler existed and avoids
/// inventing an elaborate expiry system for a subsystem that already has no
/// session-eviction story (see the TODO on
/// <see cref="DiscOnboardingSessionStore.Remove"/>).
/// </summary>
public class DiscOnboardingCompletionEventHandler : EventSubscriber
{
    private readonly DiscOnboardingSessionStore _store;
    private readonly IEventBus _eventBus;
    private readonly IDbContextFactory<MediaContext> _contextFactory;

    public DiscOnboardingCompletionEventHandler(
        IEventBus eventBus,
        DiscOnboardingSessionStore store,
        IDbContextFactory<MediaContext> contextFactory
    )
    {
        _eventBus = eventBus;
        _store = store;
        _contextFactory = contextFactory;
        Track(eventBus.Subscribe<MediaFilesScannedEvent>(OnMediaFilesScanned));
    }

    internal async Task OnMediaFilesScanned(MediaFilesScannedEvent @event, CancellationToken ct)
    {
        foreach (
            DiscOnboardingSession session in _store.All.Where(session =>
                session.State == DiscOnboardingState.Ripping
                && session.LibraryId == @event.LibraryId
                && session.ConfirmedTmdbId is not null
                && session.ConfirmedMediaType is not null
            )
        )
        {
            (string resultType, string resultId)? match = await TryMatchAsync(session, @event, ct);
            if (match is null)
                continue;

            DiscOnboardingSession completed = session.WithCompletion(
                match.Value.resultType,
                match.Value.resultId
            );
            _store.Set(completed);
            await _eventBus.PublishAsync(
                new DiscOnboardingStateChangedEvent
                {
                    StateData = DiscOnboardingStatePayload.From(completed),
                },
                ct
            );
        }
    }

    /// <summary>
    /// Matches the just-scanned movie/episode (<see cref="MediaFilesScannedEvent.MediaId"/>)
    /// against the session's confirmed TMDB target. Movie.Id and Tv.Id are the
    /// TMDB id in this codebase — a movie needs no lookup at all, its result id
    /// IS the TMDB id; a TV episode needs one lookup to read its parent show's
    /// TMDB id.
    /// </summary>
    private async Task<(string ResultType, string ResultId)?> TryMatchAsync(
        DiscOnboardingSession session,
        MediaFilesScannedEvent @event,
        CancellationToken ct
    )
    {
        if (session.ConfirmedMediaType == "movie")
        {
            return @event.MediaId == session.ConfirmedTmdbId
                ? ("movie", @event.MediaId.ToString())
                : null;
        }

        if (session.ConfirmedMediaType == "tv")
        {
            await using MediaContext context = await _contextFactory.CreateDbContextAsync(ct);
            Episode? episode = await context
                .Episodes.AsNoTracking()
                .Include(e => e.Tv)
                .FirstOrDefaultAsync(e => e.Id == @event.MediaId, ct);

            return episode is not null && episode.Tv.Id == session.ConfirmedTmdbId
                ? ("tv", episode.Id.ToString())
                : null;
        }

        return null;
    }
}
