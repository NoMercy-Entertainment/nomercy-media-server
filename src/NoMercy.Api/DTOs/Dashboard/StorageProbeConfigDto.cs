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

public record StorageProbeConfigDto
{
    [JsonProperty("server")]
    public string? Server { get; set; }

    /// <summary>
    /// Optional. When present, the probe switches from "enumerate exports"
    /// to "test-mount this export" mode and returns ok=true only if the
    /// configured export actually mounts.
    /// </summary>
    [JsonProperty("export")]
    public string? Export { get; set; }

    [JsonProperty("version")]
    public int? Version { get; set; }

    [JsonProperty("uid")]
    public int? Uid { get; set; }

    [JsonProperty("gid")]
    public int? Gid { get; set; }
}
