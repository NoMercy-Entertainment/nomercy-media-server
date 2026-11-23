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

namespace NoMercy.Api.Services.Music;

/// <summary>
/// When the join out of the playing track still stands: the transition a
/// plugin offered for it, and the hold a player put on the server's advance
/// when it started to fade.
/// <para>
/// Both belong to one track on one device. They are checked against the state
/// as it is, here, and never cleared by the code that changed the state: a
/// skip, a new list, a device that leaves and a session that ends each change
/// the state somewhere else, and none of them has to remember the join.
/// </para>
/// </summary>
internal static class MusicJoinRules
{
    /// <summary>
    /// How long past its stop an offer the players were sent is still waited
    /// for. A player that took it has started its fade by then; one that did
    /// not never will, and the offer must not stand in the way of the next.
    /// </summary>
    internal const int TakenWithinMs = 5000;

    /// <summary>
    /// Drops the offer when it no longer fits what plays, and ends a
    /// crossfade hold that was started for another track or another device.
    /// </summary>
    public static void Reconcile(MusicPlayerState state)
    {
        if (state.IsCrossfading && !HoldStillStands(state))
            state.IsCrossfading = false;

        if (state.Transition is { } offer && !OfferStillStands(state, offer))
            state.Transition = null;
    }

    /// <summary>
    /// The offer to send to the players now, or null. It is sent once, when
    /// the playing track is within <paramref name="leadMs" /> of the stop.
    /// </summary>
    public static MusicTransitionOffer? Due(MusicPlayerState state, int leadMs)
    {
        Reconcile(state);

        if (state.Transition is not { Sent: false } offer)
            return null;

        // Past the stop without having been sent: the listener sought past it.
        if (state.Time >= offer.OutgoingStopMs)
        {
            state.Transition = null;
            return null;
        }

        return state.Time >= offer.OutgoingStopMs - leadMs ? offer : null;
    }

    /// <summary>
    /// The listener sought. A fade that was going on is over. An offer the
    /// position is now past is gone; one the position is well before again is
    /// sent again when its moment comes, since a player may have let go of it.
    /// </summary>
    public static void AfterSeek(MusicPlayerState state, int leadMs)
    {
        state.IsCrossfading = false;

        if (state.Transition is not { } offer)
            return;

        if (state.Time >= offer.OutgoingStopMs)
            state.Transition = null;
        else if (state.Time < offer.OutgoingStopMs - leadMs)
            offer.Sent = false;
    }

    /// <summary>
    /// True when <paramref name="offer" /> leads out of the track that plays
    /// on the device it was made for, into what comes next.
    /// </summary>
    public static bool Fits(MusicPlayerState state, MusicTransitionOffer offer)
    {
        return state.CurrentItem?.Id == offer.FromTrackId
            && SameDevice(state.DeviceId, offer.DeviceId)
            && (
                offer.LeadsBackIntoItsTrack
                || MusicPlaybackService.PeekNextTrack(state)?.Id == offer.ToTrackId
            );
    }

    private static bool OfferStillStands(MusicPlayerState state, MusicTransitionOffer offer)
    {
        if (!Fits(state, offer))
            return false;

        // Sent, the stop well behind, and no player fading: nobody took it.
        return !offer.Sent
            || state.IsCrossfading
            || state.Time <= offer.OutgoingStopMs + TakenWithinMs;
    }

    private static bool HoldStillStands(MusicPlayerState state)
    {
        return state.CurrentItem?.Id == state.CrossfadeTrackId
            && SameDevice(state.DeviceId, state.CrossfadeHeldDeviceId);
    }

    private static bool SameDevice(string? one, string? other)
    {
        return string.Equals(one ?? "", other ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
