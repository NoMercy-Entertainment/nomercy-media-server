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
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

/// <summary>
/// What analysis measured for one track.
/// <para>
/// Its own response rather than a field on the six track DTOs: analysis is
/// absent for most rows for most of a library's life, so putting it inline
/// would grow every list response for every user to carry mostly nulls.
/// </para>
/// <para>
/// Every measurement is nullable. A partial analysis is normal — tempo in
/// particular is withheld while the detector cannot be trusted.
/// </para>
/// </summary>
public record TrackAudioAnalysisDto
{
    [JsonProperty("track_id")]
    public Guid TrackId { get; set; }

    [JsonProperty("bpm")]
    public double? Bpm { get; set; }

    [JsonProperty("bpm_confidence")]
    public double? BpmConfidence { get; set; }

    [JsonProperty("beat_offset_ms")]
    public int? BeatOffsetMs { get; set; }

    [JsonProperty("beat_interval_ms")]
    public double? BeatIntervalMs { get; set; }

    [JsonProperty("key")]
    public string? KeyName { get; set; }

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

    [JsonProperty("energy")]
    public double? Energy { get; set; }

    [JsonProperty("spectral_centroid")]
    public double? SpectralCentroid { get; set; }

    [JsonProperty("intro_end_ms")]
    public int? IntroEndMs { get; set; }

    [JsonProperty("outro_start_ms")]
    public int? OutroStartMs { get; set; }

    [JsonProperty("analyzer_version")]
    public int AnalyzerVersion { get; set; }

    public TrackAudioAnalysisDto() { }

    public TrackAudioAnalysisDto(TrackAudioAnalysis analysis)
    {
        TrackId = analysis.TrackId;
        Bpm = analysis.Bpm;
        BpmConfidence = analysis.BpmConfidence;
        BeatOffsetMs = analysis.BeatOffsetMs;
        BeatIntervalMs = analysis.BeatIntervalMs;
        KeyName = analysis.KeyName;
        KeyCamelot = analysis.KeyCamelot;
        KeyConfidence = analysis.KeyConfidence;
        IntegratedLufs = analysis.IntegratedLufs;
        TruePeakDb = analysis.TruePeakDb;
        LoudnessRange = analysis.LoudnessRange;
        Energy = analysis.Energy;
        SpectralCentroid = analysis.SpectralCentroid;
        IntroEndMs = analysis.IntroEndMs;
        OutroStartMs = analysis.OutroStartMs;
        AnalyzerVersion = analysis.AnalyzerVersion;
    }
}
