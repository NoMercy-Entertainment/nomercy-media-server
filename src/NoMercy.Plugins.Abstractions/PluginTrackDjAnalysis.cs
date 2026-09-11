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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// The measurements automix needs on top of <c>PluginTrackAudioAnalysis</c> -
/// where the bars and phrases fall, where the vocal is present, and where a
/// transition could plausibly start or land. A separate record rather than
/// more members on the base analysis: the base analysis is read by any
/// plugin through <see cref="IPluginMusicQuery" /> and computed once per
/// track; this is heavier, DJ-specific, and versioned on its own so a change
/// to phrase detection does not force every base analysis row to be
/// recomputed.
/// </summary>
/// <param name="TrackId"><c>Guid</c>, matching <c>PluginTrackAudioAnalysis.TrackId</c>.</param>
/// <param name="DjAnalyzerVersion">Which run of this analyzer produced the row.</param>
/// <param name="BaseAnalyzerVersion">
/// The <c>PluginTrackAudioAnalysis.AnalyzerVersion</c> this row was computed
/// from - beat and phrase detection both need the tempo and beat grid the
/// base analysis already found. A consumer holding a newer base analysis
/// than this can tell the DJ row is stale without re-deriving anything.
/// </param>
/// <param name="DownbeatIndex">
/// Which beat of the beat grid is the first downbeat, 0-based. Null when the
/// detector found beats but could not place the bar.
/// </param>
/// <param name="BeatsPerBar">Almost always 4; carried explicitly rather than assumed.</param>
/// <param name="PhraseLengthBars">How many bars make one phrase in this track, typically 8 or 16.</param>
/// <param name="PhraseStartsMs">Where each phrase begins, in track time.</param>
/// <param name="VocalRegionsMs">
/// Where a vocal is present, as <c>[startMs, endMs]</c> pairs. Absence of a
/// region does not claim silence, only that no vocal was detected there.
/// </param>
/// <param name="BarEnergy">
/// Short-term loudness in LUFS per bar, index = bar; not normalised, so
/// values are typically negative, roughly -60 to 0. Cheap enough to hold for
/// a whole track so a caller can plot or search an energy arc without asking
/// per bar.
/// </param>
/// <param name="CuePoints">Candidate transition points; see <see cref="PluginCuePoint" />.</param>
/// <param name="Chords">The chord timeline; see <see cref="PluginChord" />.</param>
public sealed record PluginTrackDjAnalysis(
    Guid TrackId,
    int DjAnalyzerVersion,
    int BaseAnalyzerVersion,
    int? DownbeatIndex,
    int BeatsPerBar,
    int PhraseLengthBars,
    IReadOnlyList<int> PhraseStartsMs,
    IReadOnlyList<int[]> VocalRegionsMs,
    IReadOnlyList<double> BarEnergy,
    IReadOnlyList<PluginCuePoint> CuePoints,
    IReadOnlyList<PluginChord> Chords
);
