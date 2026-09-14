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

public record FolderDriverAssignDto
{
    [JsonProperty("driver_id")]
    public string? DriverId { get; set; }

    /// <summary>
    /// Optional sub-path within the driver root. When null the existing
    /// folder path is preserved; when non-null (including empty string)
    /// it replaces the folder path.
    /// </summary>
    [JsonProperty("path")]
    public string? Path { get; set; }
}
