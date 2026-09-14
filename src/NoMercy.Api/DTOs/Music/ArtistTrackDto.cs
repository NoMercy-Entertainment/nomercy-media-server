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
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

public record ArtistTrackDto
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
    public JToken? ColorPalette { get; set; }

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

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("lyrics")]
    public Lyric[]? Lyrics { get; set; }

    [JsonProperty("album_id")]
    public Guid AlbumId { get; set; }

    [JsonProperty("album_name")]
    public string AlbumName { get; set; }

    [JsonProperty("album_track")]
    public IEnumerable<AlbumDto> Album { get; set; }

    [JsonProperty("artist_track")]
    public IEnumerable<ArtistDto> Artist { get; set; }

    public ArtistTrackDto(ArtistTrack artistTrack, string country)
    {
        Id = artistTrack.Track.Id;
        Name = artistTrack.Track.Name;
        Cover = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover is not null
            ? new Uri(
                $"/images/music{artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Cover}",
                UriKind.Relative
            ).ToString()
            : null;
        Link = new($"/music/tracks/{artistTrack.Track.Id}", UriKind.Relative);
        Path = new Uri(
            $"/{artistTrack.Track.FolderId}{artistTrack.Track.Folder}{artistTrack.Track.Filename}",
            UriKind.Relative
        ).ToString();
        Type = "track";
        ColorPalette = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album._colorPalette.ToRaw();
        Date = artistTrack.Track.Date;
        Disc = artistTrack.Track.DiscNumber;
        Track = artistTrack.Track.TrackNumber;
        Duration = artistTrack.Track.Duration;
        // AlbumTrack can be empty for orphaned tracks (rip in progress, missing
        // metadata). Fall back to Empty / blank rather than NRE — the dashboard
        // already tolerates blank album metadata for these rows.
        AlbumId = artistTrack.Track.AlbumTrack.FirstOrDefault()?.AlbumId ?? Guid.Empty;
        AlbumName = artistTrack.Track.AlbumTrack.FirstOrDefault()?.Album.Name ?? string.Empty;
        Favorite = artistTrack.Track.TrackUser.Count != 0;
        Quality = artistTrack.Track.Quality;
        Lyrics = artistTrack.Track.Lyrics;

        Album = artistTrack
            .Track.AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country));

        Artist = artistTrack
            .Track.ArtistTrack.DistinctBy(trackArtist => trackArtist.ArtistId)
            .Select(trackArtist => new ArtistDto(trackArtist, country));
    }

    public ArtistTrackDto(Track track, string? country = "US")
    {
        Id = track.Id;
        Name = track.Name;
        ColorPalette =
            track.AlbumTrack.FirstOrDefault()?.Album._colorPalette.ToRaw()
            ?? track.ArtistTrack.FirstOrDefault()?.Artist._colorPalette.ToRaw();
        Cover =
            track.AlbumTrack.FirstOrDefault()?.Album.Cover
            ?? track.ArtistTrack.FirstOrDefault()?.Artist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Path = new Uri(
            $"/{track.FolderId}{track.Folder}{track.Filename}",
            UriKind.Relative
        ).ToString();
        Type = "track";
        Date = track.UpdatedAt;
        Disc = track.DiscNumber;
        Track = track.TrackNumber;
        Duration = track.Duration;
        Favorite = track.TrackUser.Count != 0;
        Quality = track.Quality;
        AlbumName = track.AlbumTrack.FirstOrDefault()?.Album.Name ?? string.Empty;
        Link = new($"/music/tracks/{track.Id}", UriKind.Relative);

        Album = track
            .AlbumTrack.DistinctBy(trackAlbum => trackAlbum.AlbumId)
            .Select(albumTrack => new AlbumDto(albumTrack, country!));

        Artist = track
            .ArtistTrack.DistinctBy(trackArtist => trackArtist.ArtistId)
            .Select(trackArtist => new ArtistDto(trackArtist, country!));
    }
}
