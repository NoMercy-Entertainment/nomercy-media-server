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

using Newtonsoft.Json;
using NoMercy.Data.Music;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Api.DTOs.Music;

/// <summary>
/// The DJ half of a track's analysis: downbeat, phrases, vocal activity,
/// per-bar energy, cue points and chords — written by the automix plugin,
/// never by the server. Absent from <see cref="TrackAudioAnalysisDto.Dj" />
/// whenever there is no Ok row, same as every other analysis measurement.
/// </summary>
public record TrackDjAnalysisDto
{
    [JsonProperty("dj_analyzer_version")]
    public int DjAnalyzerVersion { get; set; }

    /// <summary>
    /// The base <see cref="TrackAudioAnalysisDto.AnalyzerVersion" /> this row
    /// was computed against. A consumer holding a newer base analysis than
    /// this can tell the DJ row is stale without re-deriving anything.
    /// </summary>
    [JsonProperty("base_analyzer_version")]
    public int BaseAnalyzerVersion { get; set; }

    /// <summary>Which beat of the base grid is beat 1. Null when undetected.</summary>
    [JsonProperty("downbeat_index")]
    public int? DownbeatIndex { get; set; }

    [JsonProperty("beats_per_bar")]
    public int BeatsPerBar { get; set; }

    [JsonProperty("phrase_length_bars")]
    public int PhraseLengthBars { get; set; }

    /// <summary>Where each phrase begins, in track time.</summary>
    [JsonProperty("phrase_starts_ms")]
    public List<int> PhraseStartsMs { get; set; } = [];

    /// <summary>Where a vocal is present, as [startMs, endMs] pairs.</summary>
    [JsonProperty("vocal_regions_ms")]
    public List<int[]> VocalRegionsMs { get; set; } = [];

    /// <summary>Short-term loudness in LUFS per bar, index = bar.</summary>
    [JsonProperty("bar_energy")]
    public List<double> BarEnergy { get; set; } = [];

    /// <summary>Candidate transition points, ranked by score descending.</summary>
    [JsonProperty("cue_points")]
    public List<CuePointDto> CuePoints { get; set; } = [];

    /// <summary>The chord timeline.</summary>
    [JsonProperty("chords")]
    public List<ChordDto> Chords { get; set; } = [];

    /// <summary>
    /// 0 (C) .. 11 (B), derived from the base row's key name — a minor key's
    /// tonic, e.g. "Am" reads as 9. Null when the base analysis has no key or
    /// the detector named one <see cref="CamelotKey" /> does not recognise.
    /// </summary>
    [JsonProperty("pitch_class")]
    public int? PitchClass { get; set; }

    public TrackDjAnalysisDto() { }

    /// <param name="dj">An Ok <see cref="TrackDjAnalysis" /> row.</param>
    /// <param name="baseKeyName">
    /// The matching <see cref="TrackAudioAnalysis.KeyName" />, if any — the DJ
    /// row itself carries no key of its own.
    /// </param>
    public TrackDjAnalysisDto(TrackDjAnalysis dj, string? baseKeyName)
    {
        DjAnalyzerVersion = dj.DjAnalyzerVersion;
        BaseAnalyzerVersion = dj.BaseAnalyzerVersion;
        DownbeatIndex = dj.DownbeatIndex;
        BeatsPerBar = dj.BeatsPerBar;
        PhraseLengthBars = dj.PhraseLengthBars;
        PhraseStartsMs = DjAnalysisJson.TryDeserialize<int>(dj.PhraseStartsMs) ?? [];
        VocalRegionsMs = DjAnalysisJson.TryDeserialize<int[]>(dj.VocalRegionsMs) ?? [];
        BarEnergy = DjAnalysisJson.TryDeserialize<double>(dj.BarEnergy) ?? [];
        CuePoints =
        [
            .. (
                DjAnalysisJson.TryDeserialize<DjAnalysisJson.CuePointRow>(dj.CuePoints) ?? []
            ).Select(cue => new CuePointDto
            {
                Ms = cue.Ms,
                Type = cue.Type ?? string.Empty,
                Direction = cue.Direction ?? string.Empty,
                Score = cue.Score,
            }),
        ];
        Chords =
        [
            .. (DjAnalysisJson.TryDeserialize<DjAnalysisJson.ChordRow>(dj.Chords) ?? []).Select(
                chord => new ChordDto { Ms = chord.Ms, Chord = chord.Chord ?? string.Empty }
            ),
        ];
        PitchClass = CamelotKey.PitchClassFromKeyName(baseKeyName);
    }
}
