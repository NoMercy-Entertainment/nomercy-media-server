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

using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Media;

/// <summary>
/// One audio rendition of a <see cref="VideoFile"/>, stored raw as ffprobe and
/// the HLS master report it (codec id, channel count, bits per second).
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(VideoFileId))]
[Index(nameof(Codec), nameof(Channels))]
public class VideoFileAudioTrack
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("video_file_id")]
    public Ulid VideoFileId { get; set; }

    [JsonProperty("codec")]
    public string Codec { get; set; } = string.Empty;

    [JsonProperty("profile")]
    public string? Profile { get; set; }

    [JsonProperty("channels")]
    public int Channels { get; set; }

    [JsonProperty("channel_layout")]
    public string? ChannelLayout { get; set; }

    [JsonProperty("sample_rate")]
    public int SampleRate { get; set; }

    [JsonProperty("bit_rate_kbps")]
    public long BitRateKbps { get; set; }

    [JsonProperty("language")]
    public string Language { get; set; } = string.Empty;

    [JsonProperty("is_default")]
    public bool IsDefault { get; set; }

    [JsonProperty("group_id")]
    public string? GroupId { get; set; }

    [JsonProperty("uri")]
    public string? Uri { get; set; }
}
