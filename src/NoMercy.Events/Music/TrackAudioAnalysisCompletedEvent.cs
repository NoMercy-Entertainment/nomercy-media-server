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

namespace NoMercy.Events.Music;

public sealed class TrackAudioAnalysisCompletedEvent : EventBase
{
    public override string Source => "MusicAnalysisJob";

    public required Guid TrackId { get; init; }
    public required int AnalyzerVersion { get; init; }

    /// <summary>"Ok" or "Failed", the enum's name, so the events package needs no reference to the database.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Every library the track belongs to at the moment the analysis landed.
    /// A track can be in several, and retention is decided per library, so a
    /// subscriber needs all of them rather than one representative id. Empty
    /// when the track is in no library at all.
    /// </summary>
    public required IReadOnlyList<Ulid> LibraryIds { get; init; }
}
