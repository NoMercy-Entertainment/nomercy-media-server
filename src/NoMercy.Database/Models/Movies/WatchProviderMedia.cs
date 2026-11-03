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

namespace NoMercy.Database.Models.Movies;

[PrimaryKey(nameof(Id))]
public class WatchProviderMedia : Timestamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("provider_id")]
    public int WatchProviderId { get; set; }

    [JsonProperty("country_code")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string ProviderType { get; set; } = string.Empty; // "flatrate", "buy", "rent", "ads", "free"

    [JsonProperty("link")]
    public string? Link { get; set; }

    [JsonProperty("watch_provider")]
    public WatchProvider WatchProvider { get; set; } = null!;

    [JsonProperty("movie_id")]
    public int? MovieId { get; set; }

    [JsonProperty("movie")]
    public Movie? Movie { get; set; }

    [JsonProperty("tv_id")]
    public int? TvId { get; set; }

    [JsonProperty("tv")]
    public Tv? Tv { get; set; }
}
