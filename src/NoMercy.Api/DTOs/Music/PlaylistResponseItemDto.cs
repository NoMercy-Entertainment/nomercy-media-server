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
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

public record PlaylistResponseItemDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("country")]
    public string? Country { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("library_id")]
    public Ulid? LibraryId { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("artists")]
    public IEnumerable<ArtistDto> Artists { get; set; }

    [JsonProperty("tracks")]
    public IEnumerable<PlaylistTrackDto> Tracks { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    public PlaylistResponseItemDto(Playlist playlist, string? country = "US")
    {
        ColorPalette = playlist.ColorPalette;
        Cover = MusicCover.Url(playlist.Cover);
        Description = playlist.Description;
        Id = playlist.Id;
        Name = playlist.Name;
        Link = new($"/music/playlists/{Id}", UriKind.Relative);
        Type = "playlist";
        Artists = [];

        Tracks = playlist.Tracks.Select(albumTrack => new PlaylistTrackDto(albumTrack, country!));
    }
}
