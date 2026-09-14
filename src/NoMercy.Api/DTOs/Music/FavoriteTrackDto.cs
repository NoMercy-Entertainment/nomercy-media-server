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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Music;

public class FavoriteTrackDto
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
    public JToken? ColorPalette { get; set; }

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

    [JsonProperty("album_track")]
    public IEnumerable<AlbumDto> Albums { get; set; }

    [JsonProperty("artist_track")]
    public IEnumerable<ArtistDto> Artists { get; set; }

    public FavoriteTrackDto(ArtistTrack artistTrack, string country)
    {
        Id = artistTrack.Track.Id;
        Name = artistTrack.Track.Name;
        Cover = MusicCover.UrlWhenSet(artistTrack.Track.Cover);
        Link = new($"/music/tracks/{Id}", UriKind.Relative);
        Type = "track";
        ColorPalette = artistTrack.Track._colorPalette.ToRaw();
        Year = artistTrack.Track.Date.ParseYear();

        Albums = artistTrack.Track.AlbumTrack.Select(albumTrack => new AlbumDto(
            albumTrack,
            country
        ));
        Artists = artistTrack.Track.ArtistTrack.Select(albumTrack => new ArtistDto(
            albumTrack,
            country
        ));
    }
}
