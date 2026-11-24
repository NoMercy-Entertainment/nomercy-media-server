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
/// What became of a plugin's change to a listener's upcoming tracks. Added in
/// ABI 12.14.
/// </summary>
public enum PluginMusicQueueAnswer
{
    /// <summary>The upcoming tracks are in the order asked for.</summary>
    Accepted,

    /// <summary>Nothing plays for this listener, so there is nothing to arrange.</summary>
    NothingPlaying,

    /// <summary>
    /// The track is not among the upcoming tracks, or the order names other
    /// tracks than the ones that are queued. Nothing changed.
    /// </summary>
    NotInQueue,

    /// <summary>
    /// The change would make another track the next one, and the listener's
    /// players were already told what comes next and have started to load it.
    /// Nothing changed; the change can be made again when the next track plays.
    /// </summary>
    TooLate,
}
