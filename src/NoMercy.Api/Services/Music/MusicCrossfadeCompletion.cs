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

/// <summary>What became of a player's word that its crossfade is over.</summary>
public enum MusicCrossfadeCompletion
{
    /// <summary>The server's state is at the new track, at the position the player named.</summary>
    Completed,

    /// <summary>Nothing plays for the user, or the word came from a device that did not start the fade.</summary>
    Ignored,

    /// <summary>The track named is not the one that comes next. The server advances by itself.</summary>
    UnknownTrack,

    /// <summary>The position named lies past the end of the new track. Nothing changed.</summary>
    PositionOutsideTrack,
}
