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
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.DTOs.Music;

public record AlbumsResponseTrackDto
{
    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("date")]
    public DateTime? Date { get; set; }

    [JsonProperty("disc")]
    public int? Disc { get; set; }

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("favorite")]
    public bool Favorite { get; set; }

    [JsonProperty("filename")]
    public string? Filename { get; set; }

    [JsonProperty("folder")]
    public string? Folder { get; set; }

    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("library_id")]
    public Ulid LibraryId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("origin")]
    public Guid Origin { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; }

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("track")]
    public int? Track { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("album_track")]
    public List<AlbumDto> Album { get; set; }

    [JsonProperty("artist_track")]
    public List<ArtistDto> Artist { get; set; }

    public AlbumsResponseTrackDto(AlbumTrack artistTrack, Ulid libraryId, string country)
    {
        ColorPalette = artistTrack.Track.ColorPalette;
        Cover = MusicCover.UrlWhenSet(artistTrack.Track.Cover);
        Date = artistTrack.Track.Date;
        Disc = artistTrack.Track.DiscNumber;
        Duration = artistTrack.Track.Duration;
        Favorite = artistTrack.Track.TrackUser.Count != 0;
        Filename = artistTrack.Track.Filename;
        Folder = artistTrack.Track.Folder;
        Id = artistTrack.Track.Id;
        LibraryId = libraryId;
        Name = artistTrack.Track.Name;
        Origin = Info.DeviceId;
        Path = artistTrack.Track.Folder + "/" + artistTrack.Track.Filename;
        Quality = artistTrack.Track.Quality;
        Track = artistTrack.Track.TrackNumber;
        Type = "track";
        Link = new($"/music/albums/{artistTrack.AlbumId}", UriKind.Relative);

        Album = artistTrack
            .Track.AlbumTrack.Select(albumTrack => new AlbumDto(albumTrack, country))
            .ToList();

        Artist = artistTrack
            .Track.ArtistTrack.Select(trackArtist => new ArtistDto(trackArtist, country))
            .ToList();
    }
}
