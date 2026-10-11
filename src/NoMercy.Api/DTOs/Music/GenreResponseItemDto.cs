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
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Music;

public record GenreResponseItemDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("library_id")]
    public Ulid? LibraryId { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("tracks")]
    public IEnumerable<GenreTrackDto> Tracks { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    /// <summary>The covers the genre's card shows; a genre has no image of its own.</summary>
    [JsonProperty("item_posters")]
    public GroupPoster[] ItemPosters { get; set; } = [];

    public GenreResponseItemDto(MusicGenre genre, string? country = "US")
    {
        Id = genre.Id;
        Name = genre.Name.ToTitleCase();
        Link = new($"/music/genres/{Id}", UriKind.Relative);
        Type = "genre";
        Tracks = genre
            .MusicGenreTracks.Select(genreTrack => new GenreTrackDto(genreTrack, country!))
            .OrderBy(genreTrack => genreTrack.Disc)
            .ThenBy(genreTrack => genreTrack.Track);
    }
}
