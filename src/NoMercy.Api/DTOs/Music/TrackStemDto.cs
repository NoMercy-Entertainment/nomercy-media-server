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

/// <summary>One stem file available for a track, as the automix plugin registered it.</summary>
public record TrackStemDto
{
    /// <summary>"vocals", "accompaniment"; later "drums", "bass", "other".</summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>"full", "mixIn" or "mixOut" — which part of the track this stem covers.</summary>
    [JsonProperty("coverage")]
    public string Coverage { get; set; } = string.Empty;

    /// <summary>Null when <see cref="Coverage" /> is "full".</summary>
    [JsonProperty("window_start_ms")]
    public int? WindowStartMs { get; set; }

    [JsonProperty("window_end_ms")]
    public int? WindowEndMs { get; set; }

    [JsonProperty("format")]
    public string Format { get; set; } = string.Empty;

    [JsonProperty("sample_rate")]
    public int SampleRate { get; set; }

    [JsonProperty("storage_key")]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Model and ffmpeg, e.g. "spleeter-2stems-f16@v1.0.41".</summary>
    [JsonProperty("producer_version")]
    public string ProducerVersion { get; set; } = string.Empty;

    public TrackStemDto() { }

    public TrackStemDto(TrackStem stem)
    {
        Kind = stem.Kind;
        Coverage = CoverageToken(stem.Coverage);
        WindowStartMs = stem.WindowStartMs;
        WindowEndMs = stem.WindowEndMs;
        Format = stem.Format;
        SampleRate = stem.SampleRate;
        StorageKey = stem.StorageKey;
        ProducerVersion = stem.ProducerVersion;
    }

    /// <summary>
    /// "Full" -> "full", "MixIn" -> "mixIn": the enum name with its first
    /// letter lower-cased, rather than a switch that could fall out of step
    /// with a new <see cref="StemCoverage" /> member.
    /// </summary>
    private static string CoverageToken(StemCoverage coverage)
    {
        string name = coverage.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
