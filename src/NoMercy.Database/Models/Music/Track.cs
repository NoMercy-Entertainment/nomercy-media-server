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

using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database.Infrastructure;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Database.Models.Music;

[PrimaryKey(nameof(Id))]
[Index(nameof(Name))]
[Index(nameof(Folder))]
[Index(nameof(Filename))]
[Index(nameof(TrackNumber))]
[Index(nameof(DiscNumber))]
// Non-unique on purpose: a unique constraint would fail to apply on existing
// libraries that already contain duplicate tracks. Speeds the dedup lookup; a
// unique constraint needs a separate de-dup migration first.
[Index(nameof(Filename), nameof(HostFolder))]
public class Track : ColorPaletteTimeStamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Guid Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("track")]
    public int TrackNumber { get; set; }

    [JsonProperty("disc")]
    public int DiscNumber { get; set; }

    [JsonProperty("cover")]
    public string? Cover { get; set; }

    [JsonProperty("date")]
    public DateTime? Date { get; set; }

    [JsonProperty("filename")]
    public string? Filename { get; set; }

    [JsonProperty("duration")]
    public string Duration { get; set; } = string.Empty;

    [JsonProperty("quality")]
    public int? Quality { get; set; }

    [JsonProperty("lyrics_offset")]
    public int? LyricsOffset { get; set; }

    [Column("Lyrics")]
    [System.Text.Json.Serialization.JsonIgnore]
    // ReSharper disable once InconsistentNaming
    public string? _lyrics { get; set; }

    [NotMapped]
    [JsonProperty("lyrics")]
    public Lyric[]? Lyrics
    {
        get
        {
            if (_lyrics is null)
                return null;
            try
            {
                return JsonConvert.DeserializeObject<Lyric[]>(_lyrics);
            }
            catch (Exception)
            {
                return
                [
                    .. _lyrics
                        .Split("\\n")
                        .Select(l => new Lyric { Text = Regex.Replace(l, "^\"|\"$", "") }),
                ];
            }
        }
        set => _lyrics = JsonConvert.SerializeObject(value);
    }

    [JsonProperty("folder")]
    public string? Folder
    {
        get;
        set => field = PathNormalizer.NormalizeNullable(value);
    }

    [JsonProperty("host_folder")]
    public string? HostFolder
    {
        get;
        set => field = PathNormalizer.NormalizeNullable(value);
    }

    [JsonProperty("folder_id")]
    public Ulid FolderId { get; set; }
    public Folder LibraryFolder { get; set; } = null!;

    [JsonProperty("metadata_id")]
    public Ulid? MetadataId { get; set; }
    public Metadata Metadata { get; init; } = null!;

    [JsonProperty("album_track")]
    public ICollection<AlbumTrack> AlbumTrack { get; set; } = [];

    [JsonProperty("artist_track")]
    public ICollection<ArtistTrack> ArtistTrack { get; set; } = [];

    [JsonProperty("library_track")]
    public ICollection<LibraryTrack> LibraryTrack { get; set; } = [];

    [JsonProperty("playlist_track")]
    public ICollection<PlaylistTrack> PlaylistTrack { get; set; } = [];

    [JsonProperty("images")]
    public ICollection<Image> Images { get; set; } = [];

    [JsonProperty("track_user")]
    public ICollection<TrackUser> TrackUser { get; set; } = [];

    [JsonProperty("genre_track")]
    public ICollection<MusicGenreTrack> MusicGenreTrack { get; set; } = [];

    [JsonProperty("music_plays")]
    public ICollection<MusicPlay> MusicPlays { get; set; } = [];

    [JsonProperty("audio_analysis")]
    public TrackAudioAnalysis? AudioAnalysis { get; set; }

    public string CreateFolderName()
    {
        return Name.CleanFileName();
    }

    public string CreateName()
    {
        int padding = 2;
        if (AlbumTrack.Count.ToString().Length > 2)
            padding = AlbumTrack.Count.ToString().Length;

        // Track may be orphaned during ingest (no AlbumTrack row yet) — fall
        // back to an empty album prefix instead of throwing.
        return string.Concat([
            AlbumTrack.FirstOrDefault()?.Album.Name ?? string.Empty,
            ": ",
            DiscNumber.ToString(),
            "-",
            TrackNumber.ToString().PadLeft(padding, '0'),
            " - ",
            Name,
            " NoMercy",
        ]);
    }

    public string CreateTitle()
    {
        int padding = 2;
        if (AlbumTrack.Count.ToString().Length > 2)
            padding = AlbumTrack.Count.ToString().Length;
        return string.Concat(
            TrackNumber.ToString().PadLeft(padding, '0'),
            " - ",
            Name.MusicBrainzSafeName(),
            ".NoMercy"
        );
    }
}
