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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Music;

public record AlbumsResponseItemDto
{
    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("folder")]
    public string? Folder { get; set; }

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

    public AlbumsResponseItemDto(Album album, string? country = "US")
    {
        string? description = album
            .Translations.FirstOrDefault(translation => translation.Iso31661 == country)
            ?.Description;
        Image? img = album.Images.FirstOrDefault(image => image.Type == "background");

        Description = description.OrWhenEmpty(album.Description);

        Backdrop = !string.IsNullOrEmpty(img?.FilePath)
            ? new Uri($"/images/music{img.FilePath}", UriKind.Relative).ToString()
            : null;
        Cover = MusicCover.Url(album.Cover);
        ColorPalette = album.ColorPalette;
        if (ColorPalette is not null)
            ColorPalette.Backdrop = img?.ColorPalette?.Image;
        Disambiguation = album.Disambiguation;
        Folder = album.Folder;
        Id = album.Id;
        Name = album.Name;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);

        Tracks = album
            .AlbumTrack.Select(albumTrack => albumTrack.Track)
            .Count(albumTrack => albumTrack.Duration != null);
    }

    public AlbumsResponseItemDto(AlbumCardDto album)
    {
        Description = !string.IsNullOrEmpty(album.TranslatedDescription)
            ? album.TranslatedDescription
            : album.Description;

        Backdrop = MusicCover.Url(album.BackgroundImagePath);
        Cover = MusicCover.Url(album.Cover);
        ColorPalette = ColorPalette.FromJsonOrNull(album.ColorPalette);
        if (ColorPalette is not null)
        {
            ColorPalette? bgPalette = ColorPalette.FromJsonOrNull(
                album.BackgroundImageColorPalette
            );
            ColorPalette.Backdrop = bgPalette?.Image;
        }
        Disambiguation = album.Disambiguation;
        Folder = album.Folder;
        Id = album.Id;
        Name = album.Name;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);
        Tracks = album.TrackCount;
    }
}
