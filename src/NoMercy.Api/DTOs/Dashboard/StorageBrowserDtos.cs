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

public record StorageProbeRequest
{
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("config")]
    public StorageProbeConfigDto? Config { get; set; }
}

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

public record StorageProbeResponse
{
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    [JsonProperty("exports")]
    public List<string>? Exports { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }
}

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

public record StorageListResponse
{
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("entries")]
    public List<StorageEntryDto>? Entries { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }
}

public record StorageEntryDto
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("is_directory")]
    public bool IsDirectory { get; set; }
}

public record StorageMkdirRequest
{
    [JsonProperty("driver_id")]
    public string? DriverId { get; set; }

    [JsonProperty("path")]
    public string? Path { get; set; }
}

public record StorageMkdirResponse
{
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }
}
