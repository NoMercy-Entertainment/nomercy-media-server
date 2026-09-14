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
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Users;

namespace NoMercy.Api.DTOs.Common;

public class PlaylistDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("filename")]
    public string? Filename { get; set; }

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("user_id")]
    public Guid UserId { get; set; }

    [JsonProperty("user")]
    public User User { get; set; }

    [JsonProperty("playlist_track")]
    public ICollection<PlaylistTrack> Tracks { get; set; }

    public PlaylistDto(Playlist playlist)
    {
        Id = playlist.Id;
        Name = playlist.Name;
        Description = playlist.Description;
        Cover = playlist.Cover;
        Cover = MusicCover.UrlWhenSet(Cover);
        Filename = playlist.Filename;
        Duration = playlist.Duration;
        UserId = playlist.UserId;
        User = playlist.User;
        Tracks = playlist.Tracks;
    }
}
