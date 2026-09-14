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

public record AlbumTrackDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

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

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("track")]
    public int? Track { get; set; }

    [JsonProperty("lyrics")]
    public Lyric[]? Lyrics { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("artist_track")]
    public IEnumerable<ArtistDto> Artists { get; set; }

    [JsonProperty("album_track")]
    public IEnumerable<AlbumDto> Albums { get; set; }

    public AlbumTrackDto(AlbumTrack albumTrack, string country)
    {
        Id = albumTrack.Track.Id;
        Name = albumTrack.Track.Name;
        Cover = MusicCover.UrlWhenSet(albumTrack.Album.Cover);
        Path = new Uri(
            $"/{albumTrack.Track.FolderId}{albumTrack.Track.Folder}{albumTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Type = "track";
        ColorPalette = albumTrack.Album.ColorPalette;
        Date = albumTrack.Track.Date;
        Disc = albumTrack.Track.DiscNumber;
        Duration = albumTrack.Track.Duration;
        Favorite = albumTrack.Track.TrackUser.Count != 0;
        Quality = albumTrack.Track.Quality;
        Track = albumTrack.Track.TrackNumber;
        Lyrics = albumTrack.Track.Lyrics;
        Link = new($"/music/tracks/{Id}", UriKind.Relative);

        Artists = albumTrack.Track.ArtistTrack.Select(artistTrack => new ArtistDto(
            artistTrack,
            country
        ));

        Albums = albumTrack.Track.AlbumTrack.Select(trackAlbum => new AlbumDto(
            trackAlbum,
            country
        ));
    }
}
