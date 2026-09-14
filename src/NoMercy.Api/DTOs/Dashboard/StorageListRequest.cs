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

public record StorageListRequest
{
    /// <summary>
    /// Driver instance to browse. Server resolves type, config and any
    /// credentials via IStorageFactory — the client never handles
    /// secret material.
    /// </summary>
    [JsonProperty("driver_id")]
    public string? DriverId { get; set; }

    [JsonProperty("path")]
    public string? Path { get; set; }
}
