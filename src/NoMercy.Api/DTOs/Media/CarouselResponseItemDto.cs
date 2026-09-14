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

namespace NoMercy.Api.DTOs.Media;

public record CarouselResponseItemDto
{
    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("folder")]
    public string? Folder { get; set; }

    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("library_id")]
    public Ulid? LibraryId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("track_id")]
    public string? TrackId { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("tracks")]
    public int Tracks { get; set; }

    public CarouselResponseItemDto(Artist artist)
    {
        ColorPalette = artist.ColorPalette;
        Cover = artist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Disambiguation = artist.Disambiguation;
        Description = artist.Description;
        Folder = artist.Folder.OrEmpty();
        Id = artist.Id.ToString();
        LibraryId = artist.LibraryId;
        Name = artist.Name;
        Type = "artist";
        Link = new($"/music/artists/{Id}", UriKind.Relative);

        Tracks = artist
            .ArtistTrack.Where(artistTrack => artistTrack.Track.Duration != null)
            .DistinctBy(artistTrack => artistTrack.Track.Name.ToLower())
            .Count();
    }

    public CarouselResponseItemDto(Album album)
    {
        ColorPalette = album.ColorPalette;
        Cover = album.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Disambiguation = album.Disambiguation;
        Description = album.Description;
        Folder = album.Folder.OrEmpty();
        Id = album.Id.ToString();
        LibraryId = album.LibraryId;
        Name = album.Name;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);

        Tracks = album
            .AlbumTrack.Where(albumTrack => albumTrack.Track.Duration != null)
            .DistinctBy(albumTrack => albumTrack.Track.Name.ToLower())
            .Count();
    }

    public CarouselResponseItemDto(ArtistUser artistUser)
    {
        ColorPalette = artistUser.Artist.ColorPalette;
        Cover = artistUser.Artist.Cover ?? artistUser.Artist.Images.FirstOrDefault()?.FilePath;
        Cover = MusicCover.UrlWhenSet(Cover);
        Disambiguation = artistUser.Artist.Disambiguation;
        Description = artistUser.Artist.Description;
        Folder = artistUser.Artist.Folder.OrEmpty();
        Id = artistUser.Artist.Id.ToString();
        LibraryId = artistUser.Artist.LibraryId;
        Name = artistUser.Artist.Name;
        Type = "artist";
        Link = new($"/music/artists/{Id}", UriKind.Relative);

        Tracks = artistUser
            .Artist.ArtistTrack.Where(artistTrack => artistTrack.Track.Duration != null)
            .DistinctBy(artistTrack => artistTrack.Track.Name.ToLower())
            .Count();
    }

    public CarouselResponseItemDto(AlbumUser playlist)
    {
        ColorPalette = playlist.Album.ColorPalette;
        Cover = playlist.Album.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Disambiguation = playlist.Album.Disambiguation;
        Description = playlist.Album.Description;
        Folder = playlist.Album.Folder.OrEmpty();
        Id = playlist.Album.Id.ToString();
        LibraryId = playlist.Album.LibraryId;
        Name = playlist.Album.Name;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);

        Tracks = playlist
            .Album.AlbumTrack.Where(albumTrack => albumTrack.Track.Duration != null)
            .DistinctBy(albumTrack => albumTrack.Track.Name.ToLower())
            .Count();
    }

    public CarouselResponseItemDto(Playlist playlist)
    {
        ColorPalette = playlist.ColorPalette;
        Cover = playlist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Description = playlist.Description;
        Id = playlist.Id.ToString();
        Name = playlist.Name;
        Type = "playlist";
        Link = new($"/music/playlists/{Id}", UriKind.Relative);

        Tracks = playlist
            .Tracks.Where(playlistTrack => playlistTrack.Track.Duration != null)
            .DistinctBy(playlistTrack => playlistTrack.Track.Name.ToLower())
            .Count();
    }

    public CarouselResponseItemDto(Track track)
    {
        ColorPalette = track.ColorPalette;
        Cover = track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Folder = track.Folder.OrEmpty();
        Id = track.Id.ToString();
        Name = track.Name;
        Type = "track";
        Link = new($"/music/tracks/{Id}", UriKind.Relative);
    }
}
