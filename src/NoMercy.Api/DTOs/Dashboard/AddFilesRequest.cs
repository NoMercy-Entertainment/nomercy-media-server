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

namespace NoMercy.Api.DTOs.Dashboard;

public record AddFilesRequest
{
    [JsonProperty("library_id")]
    public Ulid LibraryId { get; set; }

    [JsonProperty("folder_id")]
    public Ulid FolderId { get; set; }

    [JsonProperty("source_driver_id")]
    public string? SourceDriverId { get; set; }

    [JsonProperty("files")]
    public AddFile[] Files { get; set; } = [];
}
