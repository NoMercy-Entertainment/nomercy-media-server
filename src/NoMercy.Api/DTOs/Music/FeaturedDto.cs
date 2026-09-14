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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Music;

public class FeaturedDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("tracks")]
    public int Tracks { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    [JsonProperty("album_artist")]
    public Guid? AlbumArtist { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    public FeaturedDto(AlbumArtist albumArtist, string country)
    {
        string? description = albumArtist
            .Album.Translations.FirstOrDefault(translation => translation.Iso31661 == country)
            ?.Description;

        Id = albumArtist.Album.Id;
        Name = albumArtist.Album.Name;
        Cover = MusicCover.UrlWhenSet(albumArtist.Album.Cover);
        Disambiguation = albumArtist.Album.Disambiguation;
        Link = new($"/music/albums/{Id}", UriKind.Relative);
        Description = !string.IsNullOrEmpty(description)
            ? description
            : albumArtist.Album.Description;
        Type = "album";
        ColorPalette = albumArtist.Album.ColorPalette;
        Tracks = albumArtist.Album.AlbumTrack.Count;
        Year = albumArtist.Album.Year;

        AlbumArtist = albumArtist.ArtistId;
    }

    public FeaturedDto(Album album, string country)
    {
        string? description = album
            .Translations.FirstOrDefault(translation => translation.Iso31661 == country)
            ?.Description;

        Id = album.Id;
        Name = album.Name;
        Disambiguation = album.Disambiguation;
        Cover = MusicCover.UrlWhenSet(album.Cover);
        Link = new($"/music/artists/{Id}", UriKind.Relative);
        Type = "artist";
        Description = description.OrWhenEmpty(album.Description);

        ColorPalette = album.ColorPalette;
    }
}
