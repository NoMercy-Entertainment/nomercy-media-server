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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMTopResultCard component - search top result.
/// </summary>
public record TopResultCardData
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = null!;

    [JsonProperty("link")]
    public string Link { get; set; } = null!;

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("artists")]
    public IEnumerable<TopResultArtist> Artists { get; set; } = [];

    [JsonProperty("albums")]
    public IEnumerable<TopResultAlbum> Albums { get; set; } = [];

    [JsonProperty("track")]
    public TopResultTrack? Track { get; set; }

    public TopResultCardData() { }

    /// <summary>The top search result: the first track, else artist, else album.</summary>
    public static TopResultCardData? FirstOf(Track? track, Artist? artist, Album? album)
    {
        if (track is not null)
            return new(track);
        if (artist is not null)
            return new(artist);
        return album is null ? null : new(album);
    }

    /// <summary>The top search result: the first track, else artist, else album.</summary>
    public static TopResultCardData? FirstOf(
        SearchTrackCardDto? track,
        ArtistCardDto? artist,
        AlbumCardDto? album
    )
    {
        if (track is not null)
            return new(track);
        if (artist is not null)
            return new(artist);
        return album is null ? null : new(album);
    }

    public TopResultCardData(Artist artist)
    {
        Id = artist.Id.ToString();
        Title = artist.Name;
        Type = "artist";
        Link = $"/music/artists/{artist.Id}";
        Cover = artist.Cover;
        ColorPalette = artist.ColorPalette;
        Link = $"/music/albums/{artist.Id}";
        Type = "album";
    }

    public TopResultCardData(Album album)
    {
        Id = album.Id.ToString();
        Title = album.Name;
        Type = "album";
        Link = $"/music/albums/{album.Id}";
        Cover = album.Cover;
        ColorPalette = album.ColorPalette;
        Artists = album.AlbumArtist.Select(aa => new TopResultArtist
        {
            Id = aa.ArtistId.ToString(),
            Name = aa.Artist.Name,
            Link = new($"/music/artists/{aa.ArtistId}", UriKind.Relative),
            Type = "artist",
        });
    }

    public TopResultCardData(Track track)
    {
        Id = track.Id.ToString();
        Title = track.Name;
        Type = "track";
        Link = $"/music/tracks/{track.Id}";
        Cover = track.Cover;
        ColorPalette = track.ColorPalette;
        Artists = track.ArtistTrack.Select(at => new TopResultArtist
        {
            Id = at.ArtistId.ToString(),
            Name = at.Artist.Name,
            Link = new($"/music/artists/{at.ArtistId}", UriKind.Relative),
            Type = "artist",
        });
        Albums = track.AlbumTrack.Select(at => new TopResultAlbum
        {
            Id = at.AlbumId.ToString(),
            Name = at.Album.Name,
            Link = new($"/music/albums/{at.AlbumId}", UriKind.Relative),
            Type = "album",
        });
        Track = new()
        {
            Id = track.Id.ToString(),
            Name = track.Name,
            Duration = track.Duration,
            Path = $"/{track.FolderId}{track.Folder}{track.Filename}",
            Link = new($"/music/tracks/{track.Id}", UriKind.Relative),
            Type = "track",
            Disc = track.DiscNumber,
            Track = track.TrackNumber,
            Quality = track.Quality,
            Artists = track.ArtistTrack.Select(at => new TopResultArtist
            {
                Id = at.ArtistId.ToString(),
                Name = at.Artist.Name,
                Link = new($"/music/artists/{at.ArtistId}", UriKind.Relative),
                Type = "artist",
            }),
            Albums = track.AlbumTrack.Select(at => new TopResultAlbum
            {
                Id = at.AlbumId.ToString(),
                Name = at.Album.Name,
                Link = new($"/music/albums/{at.AlbumId}", UriKind.Relative),
                Type = "album",
            }),
        };
    }

    public TopResultCardData(SearchTrackCardDto track)
    {
        Id = track.Id.ToString();
        Title = track.Name;
        Type = "track";
        Link = $"/music/tracks/{track.Id}";
        string? cover = track.AlbumCover ?? track.ArtistCover;
        Cover = cover is not null ? $"/images/music{cover}" : null;
        string? colorPaletteStr = track.AlbumColorPalette ?? track.ArtistColorPalette;
        ColorPalette = ColorPalette.FromJsonOrNull(colorPaletteStr);
        Artists = track.Artists.Select(at => new TopResultArtist
        {
            Id = at.Id.ToString(),
            Name = at.Name,
            Link = new($"/music/artists/{at.Id}", UriKind.Relative),
            Type = "artist",
        });
        Albums = track.Albums.Select(at => new TopResultAlbum
        {
            Id = at.Id.ToString(),
            Name = at.Name,
            Link = new($"/music/albums/{at.Id}", UriKind.Relative),
            Type = "album",
        });
        Track = new()
        {
            Id = track.Id.ToString(),
            Name = track.Name,
            Duration = track.Duration,
            Path = $"/{track.FolderId}{track.Folder}{track.Filename}",
            Link = new($"/music/tracks/{track.Id}", UriKind.Relative),
            Type = "track",
            Disc = track.DiscNumber,
            Track = track.TrackNumber,
            Quality = track.Quality,
            Artists = track.Artists.Select(at => new TopResultArtist
            {
                Id = at.Id.ToString(),
                Name = at.Name,
                Link = new($"/music/artists/{at.Id}", UriKind.Relative),
                Type = "artist",
            }),
            Albums = track.Albums.Select(at => new TopResultAlbum
            {
                Id = at.Id.ToString(),
                Name = at.Name,
                Link = new($"/music/albums/{at.Id}", UriKind.Relative),
                Type = "album",
            }),
        };
    }

    public TopResultCardData(ArtistCardDto artist)
    {
        Id = artist.Id.ToString();
        Title = artist.Name;
        Type = "artist";
        Link = $"/music/artists/{artist.Id}";
        Cover = artist.Cover is not null ? $"/images/music{artist.Cover}" : null;
        ColorPalette = ColorPalette.FromJsonOrNull(artist.ColorPalette);
    }

    public TopResultCardData(AlbumCardDto album)
    {
        Id = album.Id.ToString();
        Title = album.Name;
        Type = "album";
        Link = $"/music/albums/{album.Id}";
        Cover = album.Cover is not null ? $"/images/music{album.Cover}" : null;
        ColorPalette = ColorPalette.FromJsonOrNull(album.ColorPalette);
    }
}
