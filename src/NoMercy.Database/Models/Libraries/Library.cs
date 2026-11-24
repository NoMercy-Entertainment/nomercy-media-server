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
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Libraries;

[PrimaryKey(nameof(Id))]
[Index(nameof(Id), IsUnique = true)]
[Index(nameof(Title))]
[Index(nameof(Type))]
[Index(nameof(Order))]
public class Library : Timestamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; }

    [JsonProperty("chapter_images")]
    public bool ChapterImages { get; set; }

    [JsonProperty("extract_chapters")]
    public bool ExtractChapters { get; set; }

    [JsonProperty("extract_chapters_during")]
    public bool ExtractChaptersDuring { get; set; }

    [JsonProperty("image")]
    public string? Image { get; set; }

    [JsonProperty("auto_refresh_interval")]
    public int AutoRefreshInterval { get; set; }

    [Column("AutoEncodeOnScan")]
    [JsonProperty("auto_encode_on_scan")]
    public bool AutoEncodeOnScan { get; set; }

    [Column("AutoConfirmDiscMatches")]
    [JsonProperty("auto_confirm_disc_matches")]
    public bool AutoConfirmDiscMatches { get; set; }

    [Column("EncodePresetId")]
    [JsonProperty("encode_preset_id")]
    public Ulid? EncodePresetId { get; set; }

    [JsonProperty("order")]
    public int? Order { get; set; }

    [JsonProperty("perfect_subtitle_match")]
    public bool PerfectSubtitleMatch { get; set; }

    [JsonProperty("realtime")]
    public bool Realtime { get; set; }

    [JsonProperty("special_season_name")]
    public string? SpecialSeasonName { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("folder_libraries")]
    public ICollection<FolderLibrary> FolderLibraries { get; set; } = [];

    [JsonProperty("language_libraries")]
    public ICollection<LanguageLibrary> LanguageLibraries { get; set; } = [];

    [JsonProperty("library_users")]
    public ICollection<LibraryUser> LibraryUsers { get; set; } = [];

    [JsonProperty("library_tvs")]
    public ICollection<LibraryTv> LibraryTvs { get; set; } = [];

    [JsonProperty("library_movies")]
    public ICollection<LibraryMovie> LibraryMovies { get; set; } = [];

    [JsonProperty("library_tracks")]
    public ICollection<LibraryTrack> LibraryTracks { get; set; } = [];

    [JsonProperty("collection_libraries")]
    public ICollection<CollectionLibrary> CollectionLibraries { get; set; } = [];

    [JsonProperty("album_libraries")]
    public ICollection<AlbumLibrary> AlbumLibraries { get; set; } = [];

    [JsonProperty("artist_libraries")]
    public ICollection<ArtistLibrary> ArtistLibraries { get; set; } = [];

    [JsonProperty("playback_preferences")]
    public ICollection<PlaybackPreference> PlaybackPreferences { get; set; } = [];
}
