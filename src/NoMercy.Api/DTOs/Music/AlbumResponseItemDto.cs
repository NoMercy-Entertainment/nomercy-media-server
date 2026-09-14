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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

public record AlbumResponseItemDto
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
    public IEnumerable<AlbumTrackDto> Tracks { get; set; }

    [JsonProperty("images")]
    public IEnumerable<ImageDto> Images { get; set; }

    [JsonProperty("genres")]
    public IEnumerable<GenreDto> Genres { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    public AlbumResponseItemDto(Album album, string? country = "US")
    {
        ColorPalette = album.ColorPalette;
        Cover = MusicCover.Url(album.Cover);
        Disambiguation = album.Disambiguation;
        Description = album.Description;
        Favorite = album.AlbumUser.Count != 0;
        Id = album.Id;
        LibraryId = album.LibraryId;
        Name = album.Name;
        Link = new($"/music/albums/{Id}", UriKind.Relative);
        Type = "album";

        Artists = album
            .AlbumArtist.DistinctBy(trackArtist => trackArtist.ArtistId)
            .Select(albumArtist => new ArtistDto(albumArtist, country!));

        Genres = album.AlbumMusicGenre.Select(musicGenre => new GenreDto(musicGenre));

        Images = album.Images.Select(image => new ImageDto(image));

        Tracks = album.AlbumTrack.Select(albumTrack => new AlbumTrackDto(albumTrack, country!));
    }
}
