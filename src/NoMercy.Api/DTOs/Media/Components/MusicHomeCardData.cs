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
/// Data for NMMusicHomeCard component - music home featured card (similar to MusicCardData).
/// </summary>
public record MusicHomeCardData
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

    public MusicHomeCardData() { }

    public MusicHomeCardData(Album album)
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

    public MusicHomeCardData(Artist artist)
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

    public MusicHomeCardData(TopMusicDto topMusic)
    {
        Id = topMusic.Id;
        Name = topMusic.Name;
        Cover = MusicCover.Url(topMusic.Cover);
        Type = topMusic.Type;
        Link = topMusic.Link.ToString();
        ColorPalette = topMusic.ColorPalette;
    }
}
