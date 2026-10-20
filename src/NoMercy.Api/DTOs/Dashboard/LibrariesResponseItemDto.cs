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
using NoMercy.Data.DTOs;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;

namespace NoMercy.Api.DTOs.Dashboard;

public record LibrariesResponseItemDto
{
    [JsonProperty("id")]
    public Ulid Id { get; set; }

    [JsonProperty("autoRefreshInterval")]
    public long AutoRefreshInterval { get; set; }

    [JsonProperty("chapterImages")]
    public long ChapterImages { get; set; }

    [JsonProperty("image")]
    public string? Image { get; set; }

    [JsonProperty("perfectSubtitleMatch")]
    public bool PerfectSubtitleMatch { get; set; }

    [JsonProperty("realtime")]
    public bool Realtime { get; set; }

    [JsonProperty("autoEncodeOnScan")]
    public bool AutoEncodeOnScan { get; set; }

    [JsonProperty("autoConfirmDiscMatches")]
    public bool AutoConfirmDiscMatches { get; set; }

    [JsonProperty("encodePresetId")]
    public Ulid? EncodePresetId { get; set; }

    [JsonProperty("specialSeasonName")]
    public string? SpecialSeasonName { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("order")]
    public int? Order { get; set; }

    [JsonProperty("color_palette")]
    public ColorPalette? ColorPalette { get; set; }

    [JsonProperty("pagination")]
    public string Pagination { get; set; } = "auto";

    [JsonProperty("link")]
    public Uri Link { get; set; } = null!;

    [JsonProperty("created_at")]
    public DateTime? CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonProperty("folder_library")]
    public FolderLibraryDto[] FolderLibrary { get; set; }

    [JsonProperty("subtitles")]
    public string[] Subtitles { get; set; }

    public LibrariesResponseItemDto(Library library)
    {
        bool shouldPaginate = library.LibraryMovies.Count + library.LibraryTvs.Count > 500;
        Id = library.Id;
        AutoRefreshInterval = library.AutoRefreshInterval;
        ChapterImages = library.ChapterImages ? 1 : 0;
        Image = library.Image;
        PerfectSubtitleMatch = library.PerfectSubtitleMatch;
        Realtime = library.Realtime;
        AutoEncodeOnScan = library.AutoEncodeOnScan;
        AutoConfirmDiscMatches = library.AutoConfirmDiscMatches;
        EncodePresetId = library.EncodePresetId;
        SpecialSeasonName = library.SpecialSeasonName;
        Title = library.Title;
        Type = library.Type;
        Order = library.Order;
        CreatedAt = library.CreatedAt;
        UpdatedAt = library.UpdatedAt;
        Pagination = shouldPaginate ? "letter" : "auto";
        Link = shouldPaginate
            ? new($"/libraries/{Id}/letter/A", UriKind.Relative)
            : new($"/libraries/{Id}", UriKind.Relative);
        Subtitles = library
            .LanguageLibraries.Select(languageLibrary => languageLibrary.Language.Iso6391)
            .ToArray();

        FolderLibrary = library
            .FolderLibraries.Select(folderLibrary => new FolderLibraryDto
            {
                FolderId = folderLibrary.FolderId,
                LibraryId = folderLibrary.LibraryId,
                Folder = new()
                {
                    Id = folderLibrary.Folder.Id,
                    Path = folderLibrary.Folder.Path,
                    DriverId = folderLibrary.Folder.DriverId,
                    DriverName = folderLibrary.Folder.Driver?.Name ?? string.Empty,
                    EncoderProfiles = folderLibrary
                        .Folder.EncodingPresetFolders.Where(link => link.Preset is not null)
                        .Select(link => new Data.DTOs.Encoder.FolderPresetDto
                        {
                            Id = link.Preset!.Id,
                            Name = link.Preset!.Name,
                            // See FolderDto — no Container column on the preset row.
                            Container = string.Empty,
                        })
                        .ToArray(),
                },
            })
            .ToArray();
    }
}
