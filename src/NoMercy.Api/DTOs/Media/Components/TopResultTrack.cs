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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Media.Components;

public record TopResultTrack
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("name")]
    public string Name { get; set; } = null!;

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("type")]
    public string Type { get; set; } = null!;

    [JsonProperty("disc")]
    public int Disc { get; set; }

    [JsonProperty("track")]
    public int Track { get; set; }

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("artist_track")]
    public IEnumerable<TopResultArtist> Artists { get; set; } = [];

    [JsonProperty("album_track")]
    public IEnumerable<TopResultAlbum> Albums { get; set; } = [];
}
