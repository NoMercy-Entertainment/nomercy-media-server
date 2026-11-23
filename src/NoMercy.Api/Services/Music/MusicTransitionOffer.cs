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
/// Audio a plugin offered for the join out of the track that plays for one
/// listener. The player keeps at most one, on the listener's state, until it
/// was played or no longer fits what plays.
/// </summary>
public sealed class MusicTransitionOffer
{
    /// <summary>The plugin whose file it is.</summary>
    public required Ulid PluginId { get; init; }

    /// <summary>The track the file leads out of.</summary>
    public required Guid FromTrackId { get; init; }

    /// <summary>The track the file leads into; the same track for a stretch inside one track.</summary>
    public required Guid ToTrackId { get; init; }

    /// <summary>The file, by its path relative to the plugin's own folder.</summary>
    public required string File { get; init; }

    public required int OutgoingStopMs { get; init; }

    public required int IncomingResumeMs { get; init; }

    public required int DurationMs { get; init; }

    /// <summary>A JSON object passed on to the players unread; null for none.</summary>
    public string? Data { get; init; }

    /// <summary>
    /// The device that held the session when the offer was made. Another
    /// device taking over ends the offer: it was told nothing.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>True once the listener's players were sent PrepareTransition for it.</summary>
    public bool Sent { get; set; }

    public bool LeadsBackIntoItsTrack => FromTrackId == ToTrackId;
}
