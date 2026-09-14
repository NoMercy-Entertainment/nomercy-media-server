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

public record MusicPlaylistResponseItemDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("tracks")]
    public ICollection<PlaylistTrack> Tracks { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    public MusicPlaylistResponseItemDto(Playlist playlist)
    {
        Id = playlist.Id;
        Name = playlist.Name;
        Description = playlist.Description;
        Cover = MusicCover.UrlWhenSet(playlist.Cover);
        ColorPalette = playlist.ColorPalette;
        CreatedAt = playlist.CreatedAt;
        UpdatedAt = playlist.UpdatedAt;
        Tracks = playlist.Tracks;
        Type = "playlist";
        Link = new($"/music/playlists/{Id}", UriKind.Relative);
    }
}
