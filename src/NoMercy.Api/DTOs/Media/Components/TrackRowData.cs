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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Data for NMTrackRow component - single track in a list.
/// </summary>
public record TrackRowData
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("name")]
    public string Name { get; set; } = null!;

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = null!;

    [JsonProperty("link")]
    public string Link { get; set; } = null!;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("date")]
    public string? Date { get; set; }

    [JsonProperty("disc")]
    public int? Disc { get; set; }

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("track")]
    public int? Track { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = null!;

    [JsonProperty("lyrics")]
    public IEnumerable<LyricLine>? Lyrics { get; set; }

    [JsonProperty("album_id")]
    public string AlbumId { get; set; } = null!;

    [JsonProperty("album_name")]
    public string AlbumName { get; set; } = null!;

    [JsonProperty("album_track")]
    public IEnumerable<TrackArtist> AlbumTrack { get; set; } = [];

    [JsonProperty("artist_track")]
    public IEnumerable<TrackArtist> ArtistTrack { get; set; } = [];

    public TrackRowData() { }

    public TrackRowData(Track track, bool isFavorite = false)
    {
        Id = track.Id.ToString();
        Name = track.Name;
        Cover = track.Cover;
        Path = track.Filename.OrEmpty();
        Link = $"/music/tracks/{track.Id}";
        ColorPalette = track.ColorPalette;
        Date = track.Date?.ToString("yyyy-MM-dd");
        Disc = track.DiscNumber;
        Duration = track.Duration;
        Favorite = isFavorite;
        Quality = track.Quality;
        Track = track.TrackNumber;
        Type = "track";
        AlbumId = (track.AlbumTrack.FirstOrDefault()?.AlbumId.ToString()).OrEmpty();
        AlbumName = (track.AlbumTrack.FirstOrDefault()?.Album.Name).OrEmpty();
        ArtistTrack = track.ArtistTrack.Select(at => new TrackArtist
        {
            Id = at.ArtistId.ToString(),
            Name = at.Artist.Name,
            Link = new($"/music/artists/{at.ArtistId}", UriKind.Relative),
            Type = "artist",
        });
    }

    public TrackRowData(Track track, string country)
    {
        Id = track.Id.ToString();
        Name = track.Name;
        ColorPalette =
            track.AlbumTrack.FirstOrDefault()?.Album.ColorPalette
            ?? track.ArtistTrack.FirstOrDefault()?.Artist.ColorPalette;
        string? cover =
            track.AlbumTrack.FirstOrDefault()?.Album.Cover
            ?? track.ArtistTrack.FirstOrDefault()?.Artist.Cover;
        Cover = cover is not null ? $"/images/music{cover}" : null;
        Path = $"/{track.FolderId}{track.Folder}{track.Filename}";
        Link = $"/music/tracks/{track.Id}";
        Date = track.UpdatedAt.ToString("yyyy-MM-dd");
        Disc = track.DiscNumber;
        Duration = track.Duration;
        Favorite = track.TrackUser.Count != 0;
        Quality = track.Quality;
        Track = track.TrackNumber;
        Type = "track";
        AlbumId = (track.AlbumTrack.FirstOrDefault()?.AlbumId.ToString()).OrEmpty();
        AlbumName = (track.AlbumTrack.FirstOrDefault()?.Album.Name).OrEmpty();
        ArtistTrack = track
            .ArtistTrack.DistinctBy(at => at.ArtistId)
            .Select(at => new TrackArtist
            {
                Id = at.ArtistId.ToString(),
                Name = at.Artist.Name,
                Link = new($"/music/artists/{at.ArtistId}", UriKind.Relative),
                Type = "artist",
            });
        AlbumTrack = track
            .AlbumTrack.DistinctBy(at => at.AlbumId)
            .Select(at => new TrackArtist
            {
                Id = at.AlbumId.ToString(),
                Name = at.Album.Name,
                Link = new($"/music/albums/{at.AlbumId}", UriKind.Relative),
                Type = "album",
            });
    }

    public TrackRowData(SearchTrackCardDto track)
    {
        Id = track.Id.ToString();
        Name = track.Name;
        string? colorPaletteStr = track.AlbumColorPalette ?? track.ArtistColorPalette;
        ColorPalette = ColorPalette.FromJsonOrNull(colorPaletteStr);
        string? cover = track.AlbumCover ?? track.ArtistCover;
        Cover = cover is not null ? $"/images/music{cover}" : null;
        Path = $"/{track.FolderId}{track.Folder}{track.Filename}";
        Link = $"/music/tracks/{track.Id}";
        Date = track.UpdatedAt.ToString("yyyy-MM-dd");
        Disc = track.DiscNumber;
        Duration = track.Duration;
        Favorite = track.Favorite;
        Quality = track.Quality;
        Track = track.TrackNumber;
        Type = "track";
        AlbumId = track.AlbumId.OrEmpty();
        AlbumName = track.AlbumName.OrEmpty();
        ArtistTrack = track.Artists.Select(at => new TrackArtist
        {
            Id = at.Id.ToString(),
            Name = at.Name,
            Link = new($"/music/artists/{at.Id}", UriKind.Relative),
            Type = "artist",
        });
        AlbumTrack = track.Albums.Select(at => new TrackArtist
        {
            Id = at.Id.ToString(),
            Name = at.Name,
            Link = new($"/music/albums/{at.Id}", UriKind.Relative),
            Type = "album",
        });
    }
}
