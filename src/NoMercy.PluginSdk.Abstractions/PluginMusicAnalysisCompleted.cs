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
/// The payload of <see cref="PluginTopics.MusicAnalysisCompleted" />: one
/// track's base analysis just landed, Ok or Failed.
/// </summary>
/// <param name="TrackId">Matching <c>PluginTrackAudioAnalysis.TrackId</c>.</param>
/// <param name="AnalyzerVersion">The base analyzer's version the row was computed at.</param>
/// <param name="State">The string <c>"Ok"</c> or <c>"Failed"</c>.</param>
/// <param name="LibraryIds">
/// Every library the track belonged to when the verdict landed, as the Ulid
/// text form — a topic payload crosses a plugin's own load context, so it
/// carries no host type.
/// </param>
public sealed record PluginMusicAnalysisCompleted(
    Guid TrackId,
    int AnalyzerVersion,
    string State,
    IReadOnlyList<string> LibraryIds
);
