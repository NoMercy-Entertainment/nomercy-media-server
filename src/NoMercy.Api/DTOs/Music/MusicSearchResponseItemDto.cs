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

public record MusicSearchResponseItemDto
{
    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("id")]
    public Guid Id { get; set; }

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

    public MusicSearchResponseItemDto(Artist artist)
    {
        ColorPalette = artist.ColorPalette;
        Cover = artist.Cover ?? artist.Images.FirstOrDefault()?.FilePath;
        Cover = MusicCover.Url(Cover);
        Disambiguation = artist.Disambiguation;
        Description = artist.Description;
        Id = artist.Id;
        Name = artist.Name;
        Type = "artist";
        Link = new($"/music/artists/{Id}", UriKind.Relative);

        Tracks = artist.ArtistTrack.Select(artistTrack => artistTrack.Track).Count();
    }

    public MusicSearchResponseItemDto(Album album)
    {
        ColorPalette = album.ColorPalette;
        Cover = album.Cover ?? album.Images.FirstOrDefault()?.FilePath;
        Cover = MusicCover.Url(Cover);
        Disambiguation = album.Disambiguation;
        Description = album.Description;
        Id = album.Id;
        Name = album.Name;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);

        Tracks = album.AlbumTrack.Select(artistTrack => artistTrack.Track).Count();
    }
}
