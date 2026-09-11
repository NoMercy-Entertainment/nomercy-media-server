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

using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Music;

/// <summary>
/// The DJ half of a track's analysis: downbeat, phrases, vocal activity,
/// per-bar energy, cue points and chords. Written by the automix plugin
/// through <c>IPluginMusicAnalysisWriter</c>, never by the server, and kept
/// apart from <see cref="TrackAudioAnalysis" /> so the two owners never
/// invalidate each other's rows. Lists are JSON text: a track has one to two
/// hundred bars and the planner reads a whole row at a time, so a child
/// table per bar would cost millions of rows for nothing that needs SQL.
/// </summary>
[PrimaryKey(nameof(TrackId))]
public class TrackDjAnalysis
{
    [JsonProperty("track_id")]
    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    [JsonProperty("producer_plugin_id")]
    public Ulid ProducerPluginId { get; set; }

    /// <summary>Version of the plugin's stages. A bump redoes every row.</summary>
    [JsonProperty("dj_analyzer_version")]
    public int DjAnalyzerVersion { get; set; }

    /// <summary>
    /// The <see cref="TrackAudioAnalysis.AnalyzerVersion" /> this row was
    /// computed against. When the base row moves on, this row is stale.
    /// </summary>
    [JsonProperty("base_analyzer_version")]
    public int BaseAnalyzerVersion { get; set; }

    [JsonProperty("state")]
    public AudioAnalysisState State { get; set; }

    [MaxLength(1024)]
    [JsonProperty("failure_reason")]
    public string? FailureReason { get; set; }

    /// <summary>Which beat of the base grid is beat 1: 0 to BeatsPerBar − 1.</summary>
    [JsonProperty("downbeat_index")]
    public int? DownbeatIndex { get; set; }

    [JsonProperty("beats_per_bar")]
    public int BeatsPerBar { get; set; } = 4;

    [JsonProperty("phrase_length_bars")]
    public int PhraseLengthBars { get; set; } = 8;

    /// <summary>JSON <c>int[]</c>: phrase boundaries in milliseconds from the start.</summary>
    [MaxLength(65536)]
    [JsonProperty("phrase_starts_ms")]
    public string? PhraseStartsMs { get; set; }

    /// <summary>JSON <c>[start, end][]</c> in milliseconds, regions of at least one second.</summary>
    [MaxLength(65536)]
    [JsonProperty("vocal_regions_ms")]
    public string? VocalRegionsMs { get; set; }

    /// <summary>JSON <c>double[]</c>: short-term loudness in LUFS per bar, index = bar.</summary>
    [MaxLength(65536)]
    [JsonProperty("bar_energy")]
    public string? BarEnergy { get; set; }

    /// <summary>JSON <c>{ ms, type, direction, score }[]</c>, ranked by score descending.</summary>
    [MaxLength(65536)]
    [JsonProperty("cue_points")]
    public string? CuePoints { get; set; }

    /// <summary>JSON <c>{ ms, chord }[]</c>.</summary>
    [MaxLength(65536)]
    [JsonProperty("chords")]
    public string? Chords { get; set; }

    [JsonProperty("analyzed_at")]
    public DateTime AnalyzedAt { get; set; }
}
