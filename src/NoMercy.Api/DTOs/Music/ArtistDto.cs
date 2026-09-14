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
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Music;

namespace NoMercy.Api.DTOs.Music;

public class ArtistDto
{
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("disambiguation")]
    public string? Disambiguation { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("backdrop")]
    public string? Backdrop { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("color_palette")]
    public JToken? ColorPalette { get; set; }

    [JsonProperty("link")]
    public Uri Link { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    public ArtistDto(AlbumArtist albumArtist, string country)
    {
        string? description = albumArtist
            .Artist.Translations.FirstOrDefault(translation => translation.Iso31661 == country)
            ?.Description;

        Image? img = albumArtist.Artist.Images.FirstOrDefault(image => image.Type == "background");

        Id = albumArtist.Artist.Id;
        Name = albumArtist.Artist.Name;
        Description = !string.IsNullOrEmpty(description)
            ? description
            : albumArtist.Artist.Description;
        Disambiguation = albumArtist.Artist.Disambiguation;
        Cover = MusicCover.UrlWhenSet(albumArtist.Artist.Cover);
        Backdrop = img?.FilePath is not null
            ? new Uri($"/images/music{img.FilePath}", UriKind.Relative).ToString()
            : null;
        Link = new($"/music/artists/{Id}", UriKind.Relative);
        Type = "artist";

        ColorPalette = albumArtist.Artist._colorPalette.ToRaw();
    }

    public ArtistDto(ArtistTrack artistTrack, string country)
    {
        string? description = artistTrack
            .Artist.Translations.FirstOrDefault(translation => translation.Iso31661 == country)
            ?.Description;
        Image? img = artistTrack.Artist.Images.FirstOrDefault(image => image.Type == "background");

        Id = artistTrack.Artist.Id;
        Name = artistTrack.Artist.Name;
        Description = !string.IsNullOrEmpty(description)
            ? description
            : artistTrack.Artist.Description;
        Disambiguation = artistTrack.Artist.Disambiguation;
        Cover = MusicCover.UrlWhenSet(artistTrack.Artist.Cover);
        Backdrop = img?.FilePath is not null
            ? new Uri($"/images/music{img.FilePath}", UriKind.Relative).ToString()
            : null;
        Link = new($"/music/artists/{Id}", UriKind.Relative);
        Description = artistTrack.Artist.Description;
        Type = "artist";
        Disambiguation = artistTrack.Artist.Disambiguation;
        ColorPalette = artistTrack.Artist._colorPalette.ToRaw();
    }

    /// <summary>
    /// A copy for the per-broadcast queue projection with the fields no client
    /// renders from a nested queue-entry artist nulled out: the artist
    /// <c>color_palette</c> (a full swatch graph) and <c>description</c> (an
    /// unbounded bio). Mirrors the queue-entry lyric strip in
    /// <see cref="MusicPlayerState.CloneForBroadcast"/> — together they are the
    /// bulk of the wire weight a long queue re-broadcasts every ~5s. Returns a
    /// throwaway shallow copy; the stored DTO is never mutated.
    /// </summary>
    public ArtistDto ForBroadcastQueueEntry()
    {
        ArtistDto copy = (ArtistDto)MemberwiseClone();
        copy.ColorPalette = null;
        copy.Description = null;
        return copy;
    }
}
