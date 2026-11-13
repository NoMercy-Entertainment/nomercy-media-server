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

/// <summary>
/// A client's report that its player could not start an item. The type, list id and item id
/// are the same three values the client gave the playback hub to start it.
/// </summary>
public record PlaybackFailureReportRequest
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("list_id")]
    public string? ListId { get; set; }

    [JsonProperty("item_id")]
    public string? ItemId { get; set; }

    [JsonProperty("error_code")]
    public string? ErrorCode { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }
}
