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
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Movies;

[PrimaryKey(nameof(Id))]
[Index(nameof(MediaId), nameof(TvFromId), IsUnique = true)]
[Index(nameof(MediaId), nameof(MovieFromId), IsUnique = true)]
public class Recommendation : ColorPaletteTimeStamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [MaxLength(4096)]
    [JsonProperty("overview")]
    public string? Overview { get; set; }

    [JsonProperty("poster")]
    public string? Poster { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("titleSort")]
    public string? TitleSort { get; set; }

    [JsonProperty("mediaId")]
    public int MediaId { get; set; }

    // Not stored: set from the owned title; unset (unknown) stays hidden.
    [NotMapped]
    [JsonIgnore]
    public bool? Adult { get; set; }

    [ForeignKey("TvFromId")]
    public int? TvFromId { get; set; }

    [JsonIgnore]
    public Tv? TvFrom { get; set; }

    [ForeignKey("TvToId")]
    public int? TvToId { get; set; }

    [JsonIgnore]
    public Tv? TvTo { get; set; }

    [ForeignKey("RecommendationFrom")]
    public int? MovieFromId { get; set; }

    [JsonIgnore]
    public Movie? MovieFrom { get; set; }

    [ForeignKey("RecommendationTo")]
    public int? MovieToId { get; set; }

    [JsonIgnore]
    public Movie? MovieTo { get; set; }
}
