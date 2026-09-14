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

namespace NoMercy.Api.DTOs.Dashboard;

public record FolderDriverInfoDto
{
    [JsonProperty("driver_id")]
    public string? DriverId { get; set; }

    [JsonProperty("driver_name")]
    public string? DriverName { get; set; }

    [JsonProperty("driver_type")]
    public string? DriverType { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;
}
