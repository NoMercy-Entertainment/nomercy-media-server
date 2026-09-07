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
/// What listening to a track measured: key, loudness, tempo and cue points.
/// <para>
/// A side table rather than columns on <see cref="Track" />. Analysis is absent
/// for most of a library's life and <see cref="Track" /> is already large and
/// already serialized to every client, so ten nullable columns there would cost
/// every response to serve a minority of rows.
/// </para>
/// <para>
/// Every measurement is nullable on purpose. A partial analysis is the normal
/// case, not a defect: tempo in particular is withheld while the detector is
/// unreliable, and the row is still worth keeping for its key and loudness.
/// </para>
/// </summary>
// No index beyond the key. The sweep asks "does this track have a current
// verdict" as a correlated EXISTS keyed on TrackId, so the primary key already
// serves it as a seek — confirmed against EXPLAIN QUERY PLAN, not assumed. An
// (AnalyzerVersion, State) index was written first and the planner never chose
// it, leaving only its write cost on every analyzed row.
[PrimaryKey(nameof(TrackId))]
public class TrackAudioAnalysis
{
    [JsonProperty("track_id")]
    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    /// <summary>
    /// Which analyzer produced this row. Bumping it re-queues only the rows the
    /// change invalidates, instead of forcing a full library rescan.
    /// </summary>
    [JsonProperty("analyzer_version")]
    public int AnalyzerVersion { get; set; }

    [JsonProperty("state")]
    public AudioAnalysisState State { get; set; }

    [MaxLength(1024)]
    [JsonProperty("failure_reason")]
    public string? FailureReason { get; set; }

    [JsonProperty("bpm")]
    public double? Bpm { get; set; }

    [JsonProperty("bpm_confidence")]
    public double? BpmConfidence { get; set; }

    /// <summary>First detected downbeat. Tempo without phase cannot align anything.</summary>
    [JsonProperty("beat_offset_ms")]
    public int? BeatOffsetMs { get; set; }

    [JsonProperty("beat_interval_ms")]
    public double? BeatIntervalMs { get; set; }

    /// <summary>The key as the detector named it: "C", "F#", "Am".</summary>
    [MaxLength(8)]
    [JsonProperty("key_name")]
    public string? KeyName { get; set; }

    /// <summary>The same key in Camelot notation: "8A". Derived here, not measured.</summary>
    [MaxLength(4)]
    [JsonProperty("key_camelot")]
    public string? KeyCamelot { get; set; }

    [JsonProperty("key_confidence")]
    public double? KeyConfidence { get; set; }

    [JsonProperty("integrated_lufs")]
    public double? IntegratedLufs { get; set; }

    [JsonProperty("true_peak_db")]
    public double? TruePeakDb { get; set; }

    [JsonProperty("loudness_range")]
    public double? LoudnessRange { get; set; }

    /// <summary>
    /// A 0..1 judgment, not a measurement. Derived from
    /// <see cref="IntegratedLufs" /> and <see cref="SpectralCentroid" />, both
    /// of which are stored so a consumer that disagrees can recompute. Changing
    /// how it is derived is an <see cref="AnalyzerVersion" /> bump.
    /// </summary>
    [JsonProperty("energy")]
    public double? Energy { get; set; }

    [JsonProperty("spectral_centroid")]
    public double? SpectralCentroid { get; set; }

    /// <summary>
    /// End of leading silence. A trim point, not a musical phrase boundary.
    /// </summary>
    [JsonProperty("intro_end_ms")]
    public int? IntroEndMs { get; set; }

    /// <summary>Start of trailing silence. A trim point, as above.</summary>
    [JsonProperty("outro_start_ms")]
    public int? OutroStartMs { get; set; }

    [JsonProperty("analyzed_at")]
    public DateTime AnalyzedAt { get; set; }
}
