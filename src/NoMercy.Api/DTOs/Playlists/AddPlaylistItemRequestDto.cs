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

namespace NoMercy.Api.DTOs.Playlists;

/// <summary>POST /api/v1/playlists/{id}/items request body.</summary>
public record AddPlaylistItemRequestDto
{
    [JsonProperty("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonProperty("media_id")]
    public string MediaId { get; set; } = string.Empty;

    [JsonProperty("order")]
    public int? Order { get; set; }
}
