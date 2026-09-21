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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Music;

public record PlaylistTrackDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("date")]
    public DateTime? Date { get; set; }

    [JsonProperty("disc")]
    public int? Disc { get; set; }

    [JsonProperty("track")]
    public int? Track { get; set; }

    [JsonProperty("duration")]
    public string Duration { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("album_name")]
    public string? AlbumName { get; set; }

    [JsonProperty("lyrics")]
    public Lyric[]? Lyrics { get; set; }

    [JsonProperty("album_track")]
    public List<AlbumDto> Album { get; set; }

    [JsonProperty("artist_track")]
    public List<ArtistDto> Artist { get; set; }

    /// <summary>
    /// The plugin this item belongs to, or null for server media.
    /// <para>
    /// Every device in the session renders the frame rather than its own queue,
    /// so a plugin's station has to arrive with everything a device needs to
    /// play it and nothing a device would have to ask the plugin for.
    /// </para>
    /// </summary>
    [JsonProperty("plugin_id")]
    public string? PluginId { get; set; }

    /// <summary>Host minted and user bound, which is why it carries no query.</summary>
    [JsonProperty("proxy_url")]
    public Uri? ProxyUrl { get; set; }

    /// <summary>Whether the item has no end, which decides what the transport draws.</summary>
    [JsonProperty("live")]
    public bool Live { get; set; }

    private static Image? ResolveBackdropImage(Track track, Album? primaryAlbum)
    {
        Image? trackArtistBackdrop = track
            .ArtistTrack.Select(artistTrack =>
                artistTrack.Artist.Images.FirstOrDefault(image => image.Type == "background")
            )
            .FirstOrDefault(image => image is not null);

        return trackArtistBackdrop
            ?? primaryAlbum
                ?.AlbumArtist.FirstOrDefault()
                ?.Artist.Images.FirstOrDefault(image => image.Type == "background");
    }

    /// <summary>
    /// For the serializer, and for the frame assertions that are the contract
    /// four clients read. A real track comes from the constructor below.
    /// </summary>
    public PlaylistTrackDto()
    {
        Name = string.Empty;
        Path = string.Empty;
        Duration = string.Empty;
        Type = string.Empty;
        Link = new Uri("/", UriKind.Relative);
        Album = [];
        Artist = [];
    }

    public PlaylistTrackDto(Track track, string country)
    {
        Image? img = ResolveBackdropImage(track, track.AlbumTrack.FirstOrDefault()?.Album);
        Id = track.Id;
        Name = track.Name;
        Backdrop = MusicCover.UrlWhenSet(img?.FilePath);
        Cover = track.AlbumTrack.FirstOrDefault()?.Album.Cover ?? track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{track.FolderId}{track.Folder}{track.Filename}",
            UriKind.Relative
        ).ToString();
        Link = new($"/music/tracks/{track.Id}", UriKind.Relative);
        ColorPalette = track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Date = track.Date;
        Disc = track.DiscNumber;
        Track = track.TrackNumber;
        Duration = track.Duration;
        Favorite = track.TrackUser.Count != 0;
        Quality = track.Quality;
        Lyrics = track.Lyrics;
        Type = "track";
        AlbumName = track.AlbumTrack.FirstOrDefault()?.Album.Name;

        Album = track
            .AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = track
            .ArtistTrack.Select(artistTrack => new ArtistDto(artistTrack, country))
            .ToList();
    }

    public PlaylistTrackDto(ArtistTrack artistTrack, string country)
    {
        Image? img = ResolveBackdropImage(
            artistTrack.Track,
            artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album
        );
        Id = artistTrack.Track.Id;
        Name = artistTrack.Track.Name;
        Backdrop = MusicCover.UrlWhenSet(img?.FilePath);
        Cover =
            artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover ?? artistTrack.Track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{artistTrack.Track.FolderId}{artistTrack.Track.Folder}{artistTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Link = new($"/music/tracks/{artistTrack.Track.Id}", UriKind.Relative);

        ColorPalette = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Date = artistTrack.Track.Date;
        Disc = artistTrack.Track.DiscNumber;
        Track = artistTrack.Track.TrackNumber;
        Duration = artistTrack.Track.Duration;
        Favorite = artistTrack.Track.TrackUser.Count != 0;
        Quality = artistTrack.Track.Quality;
        Lyrics = artistTrack.Track.Lyrics;
        Type = "track";
        AlbumName = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Name;

        Album = artistTrack
            .Track.AlbumTrack!.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = artistTrack
            .Track.ArtistTrack.Where(at => at.TrackId == artistTrack.TrackId)
            .Select(at => new ArtistDto(at, country))
            .ToList();
    }

    public PlaylistTrackDto(PlaylistTrack trackTrack, string country)
    {
        Image? img = ResolveBackdropImage(
            trackTrack.Track,
            trackTrack.Track.AlbumTrack.FirstOrDefault()?.Album
        );
        Id = trackTrack.Track.Id;
        Name = trackTrack.Track.Name;
        Backdrop = MusicCover.UrlWhenSet(img?.FilePath);
        Cover = trackTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover ?? trackTrack.Track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{trackTrack.Track.FolderId}{trackTrack.Track.Folder}{trackTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Link = new($"/music/tracks/{trackTrack.Track.Id}", UriKind.Relative);
        ColorPalette = trackTrack.Track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Date = trackTrack.Track.Date;
        Disc = trackTrack.Track.DiscNumber;
        Track = trackTrack.Track.TrackNumber;
        Duration = trackTrack.Track.Duration;
        Favorite = trackTrack.Track.TrackUser.Count != 0;
        Quality = trackTrack.Track.Quality;
        Lyrics = trackTrack.Track.Lyrics;
        Type = "track";
        AlbumName = trackTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Name;

        Album = trackTrack
            .Track.AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = trackTrack
            .Track.ArtistTrack.Select(albumTrack => new ArtistDto(albumTrack, country))
            .ToList();
    }

    public PlaylistTrackDto(AlbumTrack artistTrack, string country)
    {
        Image? img = ResolveBackdropImage(
            artistTrack.Track,
            artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album
        );
        Id = artistTrack.Track.Id;
        Name = artistTrack.Track.Name;
        Backdrop = MusicCover.UrlWhenSet(img?.FilePath);
        Cover =
            artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover ?? artistTrack.Track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{artistTrack.Track.FolderId}{artistTrack.Track.Folder}{artistTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Link = new($"/music/tracks/{artistTrack.Track.Id}", UriKind.Relative);

        ColorPalette = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Date = artistTrack.Track.Date;
        Disc = artistTrack.Track.DiscNumber;
        Track = artistTrack.Track.TrackNumber;
        Duration = artistTrack.Track.Duration;
        Favorite = artistTrack.Track.TrackUser.Count != 0;
        Quality = artistTrack.Track.Quality;
        Lyrics = artistTrack.Track.Lyrics;
        Type = "track";
        AlbumName = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Name;

        Album = artistTrack
            .Track.AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = artistTrack
            .Track.ArtistTrack.Select(albumTrack => new ArtistDto(albumTrack, country))
            .ToList();
    }

    public PlaylistTrackDto(MusicGenreTrack genreTrack, string country)
    {
        Image? img = ResolveBackdropImage(
            genreTrack.Track,
            genreTrack.Track.AlbumTrack.FirstOrDefault()?.Album
        );
        Id = genreTrack.Track.Id;
        Name = genreTrack.Track.Name.ToTitleCase();
        Backdrop = MusicCover.UrlWhenSet(img?.FilePath);
        Cover = genreTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover ?? genreTrack.Track.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{genreTrack.Track.FolderId}{genreTrack.Track.Folder}{genreTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Link = new($"/music/tracks/{genreTrack.Track.Id}", UriKind.Relative);
        ColorPalette = genreTrack.Track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Date = genreTrack.Track.Date;
        Disc = genreTrack.Track.DiscNumber;
        Track = genreTrack.Track.TrackNumber;
        Duration = genreTrack.Track.Duration;
        Favorite = genreTrack.Track.TrackUser.Count != 0;
        Quality = genreTrack.Track.Quality;
        Lyrics = genreTrack.Track.Lyrics;
        Type = "track";
        AlbumName = genreTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Name;

        Album = genreTrack
            .Track.AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = genreTrack
            .Track.ArtistTrack.Select(artistTrack => new ArtistDto(artistTrack, country))
            .ToList();
    }
}
