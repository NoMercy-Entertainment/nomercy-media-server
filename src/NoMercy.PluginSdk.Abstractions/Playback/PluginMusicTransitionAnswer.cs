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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What became of a transition a plugin offered for a listener. Added in ABI
/// 12.14.
/// </summary>
public enum PluginMusicTransitionAnswer
{
    /// <summary>
    /// The server holds the offer and sends it to the listener's players ten
    /// seconds before the playing track reaches its stop.
    /// </summary>
    Accepted,

    /// <summary>Nothing plays for this listener.</summary>
    NothingPlaying,

    /// <summary>The track the offer leads out of is not the track that plays.</summary>
    NotThePlayingTrack,

    /// <summary>
    /// The track the offer leads into is neither the listener's next track
    /// nor the playing track itself.
    /// </summary>
    NotTheNextTrack,

    /// <summary>The stop or the resume position lies past the end of its track.</summary>
    OutsideTheTrack,

    /// <summary>
    /// The playing track is already at or past the stop, or the listener's
    /// players were already told what comes next. Offer again when the next
    /// track plays.
    /// </summary>
    TooLate,
}
