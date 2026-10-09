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

using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Common;

[PrimaryKey(nameof(Id))]
[Index(nameof(Name))]
public class AnimeTheme
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    public ICollection<AnimeThemeMovie> AnimeThemeMovies { get; set; } = [];
    public ICollection<AnimeThemeTv> AnimeThemeTvShows { get; set; } = [];

    [JsonProperty("translations")]
    public ICollection<Translation> Translations { get; set; } = [];
}
