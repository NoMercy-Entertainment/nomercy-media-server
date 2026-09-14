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
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Media;

public record TopMusicDto
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; } = "albums";

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    public TopMusicDto()
    {
        //
    }

    public TopMusicDto(PlaylistTrack musicPlay)
    {
        Id = musicPlay.Playlist.Id.ToString();
        Name = musicPlay.Playlist.Name;
        ColorPalette = musicPlay.Playlist.ColorPalette;
        Type = "playlist";
        Link = new($"/music/playlists/{Id}", UriKind.Relative);
        Cover = musicPlay.Playlist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
    }

    public TopMusicDto(AlbumTrack albumTrack)
    {
        Id = albumTrack.Album.Id.ToString();
        Name = albumTrack.Album.Name;
        ColorPalette = albumTrack.Album.ColorPalette;
        Type = "album";
        Link = new($"/music/albums/{Id}", UriKind.Relative);
        Cover = albumTrack.Album.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
    }

    public TopMusicDto(ArtistTrack artistTrack)
    {
        Id = artistTrack.Artist.Id.ToString();
        Name = artistTrack.Artist.Name;
        ColorPalette = artistTrack.Artist.ColorPalette;
        Type = "artist";
        Link = new($"/music/artists/{Id}", UriKind.Relative);
        Cover = artistTrack.Artist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
    }

    public TopMusicDto(TopMusicItemDto item)
    {
        Id = item.Id;
        Name = item.Name;
        ColorPalette = ColorPalette.FromJsonOrNull(item.ColorPalette);
        Type = item.Type;
        Link = item.Link;
        Cover = item.Cover;
        Cover = Cover is not null ? new Uri(Cover, UriKind.Relative).ToString() : null;
    }
}
