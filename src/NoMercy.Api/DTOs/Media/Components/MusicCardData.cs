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
using NoMercy.NmSystem.Extensions;
using CarouselResponseItemDtoRepository = NoMercy.Data.DTOs.CarouselResponseItemDto;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMMusicCard component - music album/artist card.
/// </summary>
public record MusicCardData
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("name")]
    public string Name { get; set; } = null!;

    [JsonProperty("link")]
    public string Link { get; set; } = null!;

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("logo")]
    public string? Logo { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("favorite")]
    public bool? Favorite { get; set; }

    [JsonProperty("folder")]
    public string? Folder { get; set; }

    [JsonProperty("libraryID")]
    public string? LibraryId { get; set; }

    [JsonProperty("trackID")]
    public string? TrackId { get; set; }

    [JsonProperty("tracks")]
    public long? Tracks { get; set; }

    [JsonProperty("year")]
    public int? Year { get; set; }

    public MusicCardData() { }

    public MusicCardData(Album album)
    {
        Id = album.Id.ToString();
        Name = album.Name;
        Cover = MusicCover.Url(album.Cover);
        Type = "album";
        Link = $"/music/albums/{album.Id}";
        ColorPalette = album.ColorPalette;
        Year = album.Year;
        Tracks = album.AlbumTrack.Count;
        LibraryId = album.LibraryId.ToString();
    }

    public MusicCardData(Artist artist)
    {
        Id = artist.Id.ToString();
        Name = artist.Name;
        Cover = MusicCover.Url(artist.Cover);
        Type = "artist";
        Link = $"/music/artists/{artist.Id}";
        ColorPalette = artist.ColorPalette;
        Disambiguation = artist.Disambiguation;
        Description = artist.Description;
        Tracks = artist.ArtistTrack.Count;
        LibraryId = artist.LibraryId.ToString();
    }

    public MusicCardData(Playlist playlist)
    {
        Id = playlist.Id.ToString();
        Name = playlist.Name;
        Cover = MusicCover.Url(playlist.Cover);
        Type = "playlist";
        Link = $"/music/playlists/{playlist.Id}";
        Tracks = playlist.Tracks.Count;
    }

    public MusicCardData(Track track)
    {
        Id = track.Id.ToString();
        Name = track.Name;
        Type = "track";
        Link = $"/music/tracks/{track.Id}";
        Tracks = 1;
        TrackId = track.Id.ToString();
    }

    public MusicCardData(CarouselResponseItemDto carousel)
    {
        Id = carousel.Id;
        Name = carousel.Name;
        Cover = MusicCover.Url(carousel.Cover);
        Type = carousel.Type;
        Link = carousel.Link.ToString();
        Tracks = carousel.Tracks;
        ColorPalette = carousel.ColorPalette;
    }

    public MusicCardData(CarouselResponseItemDtoRepository carousel)
    {
        Id = carousel.Id;
        Name = carousel.Name.ToTitleCase();
        Cover = MusicCover.Url(carousel.Cover);
        Type = carousel.Type;
        Link = carousel.Link.ToString();
        Tracks = carousel.Tracks;
        ColorPalette = carousel.ColorPalette;
    }

    public MusicCardData(ArtistCardDto artist)
    {
        Id = artist.Id.ToString();
        Name = artist.Name;
        Cover = MusicCover.Url(artist.Cover);
        Type = "artist";
        Link = $"/music/artists/{artist.Id}";
        ColorPalette = ColorPalette.FromJsonOrNull(artist.ColorPalette);
        Disambiguation = artist.Disambiguation;
        Description = artist.Description;
        Tracks = artist.TrackCount;
        LibraryId = artist.LibraryId?.ToString();
    }

    public MusicCardData(AlbumCardDto album)
    {
        Id = album.Id.ToString();
        Name = album.Name;
        Cover = MusicCover.Url(album.Cover);
        Type = "album";
        Link = $"/music/albums/{album.Id}";
        ColorPalette = ColorPalette.FromJsonOrNull(album.ColorPalette);
        Year = album.Year;
        Tracks = album.TrackCount;
        LibraryId = album.LibraryId.ToString();
    }

    public MusicCardData(PlaylistCardDto playlist)
    {
        Id = playlist.Id.ToString();
        Name = playlist.Name;
        Cover = MusicCover.Url(playlist.Cover);
        Type = "playlist";
        Link = $"/music/playlists/{playlist.Id}";
        ColorPalette = ColorPalette.FromJsonOrNull(playlist.ColorPalette);
        Tracks = playlist.TrackCount;
    }

    public MusicCardData(MusicGenreCardDto genre)
    {
        Id = genre.Id.ToString();
        Name = genre.Name.ToTitleCase();
        Type = "genre";
        Link = $"/music/genres/{genre.Id}";
        Tracks = genre.TrackCount;
    }
}
