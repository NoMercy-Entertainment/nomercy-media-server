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

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

public class IncompleteEncodeDto
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("media_id")]
    public long MediaId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("missing_renditions")]
    public string[] MissingRenditions { get; set; } = [];

    [JsonProperty("last_error")]
    public string? LastError { get; set; }

    [JsonProperty("attempts_made")]
    public int AttemptsMade { get; set; }

    [JsonProperty("last_seen_at")]
    public DateTime LastSeenAt { get; set; }
}
