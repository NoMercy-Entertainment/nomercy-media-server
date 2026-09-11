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
/// One stem file of one track: which kind, what it covers, who produced it
/// and where it lives in the derived store. The file itself is addressed by
/// content, so the row carries a key and never a path.
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(TrackId), nameof(Kind), nameof(Coverage), nameof(ProducerVersion), IsUnique = true)]
public class TrackStem
{
    [JsonProperty("id")]
    public Ulid Id { get; set; }

    [JsonProperty("track_id")]
    public Guid TrackId { get; set; }

    public Track Track { get; set; } = null!;

    /// <summary><c>vocals</c>, <c>accompaniment</c>; later <c>drums</c>, <c>bass</c>, <c>other</c>.</summary>
    [MaxLength(16)]
    [JsonProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonProperty("coverage")]
    public StemCoverage Coverage { get; set; }

    /// <summary>Both null when <see cref="Coverage" /> is <see cref="StemCoverage.Full" />.</summary>
    [JsonProperty("window_start_ms")]
    public int? WindowStartMs { get; set; }

    [JsonProperty("window_end_ms")]
    public int? WindowEndMs { get; set; }

    [MaxLength(8)]
    [JsonProperty("format")]
    public string Format { get; set; } = "opus";

    [JsonProperty("sample_rate")]
    public int SampleRate { get; set; }

    [MaxLength(64)]
    [JsonProperty("storage_key")]
    public string StorageKey { get; set; } = string.Empty;

    public DerivedAudio DerivedAudio { get; set; } = null!;

    /// <summary>Model and ffmpeg, e.g. <c>spleeter-2stems-f16@v1.0.41</c>.</summary>
    [MaxLength(64)]
    [JsonProperty("producer_version")]
    public string ProducerVersion { get; set; } = string.Empty;

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }
}
