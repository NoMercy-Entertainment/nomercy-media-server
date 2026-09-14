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

namespace NoMercy.Api.DTOs.Management;

public record ManagementStatusDto
{
    [JsonProperty("status")]
    public string Status { get; set; } = "ok";

    [JsonProperty("server_name")]
    public string ServerName { get; set; } = string.Empty;

    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonProperty("architecture")]
    public string Architecture { get; set; } = string.Empty;

    [JsonProperty("os")]
    public string Os { get; set; } = string.Empty;

    [JsonProperty("uptime_seconds")]
    public long UptimeSeconds { get; set; }

    [JsonProperty("start_time")]
    public DateTime StartTime { get; set; }

    [JsonProperty("is_dev")]
    public bool IsDev { get; set; }

    [JsonProperty("auto_start")]
    public bool AutoStart { get; set; }

    [JsonProperty("is_docker")]
    public bool IsDocker { get; set; }

    [JsonProperty("update_available")]
    public bool UpdateAvailable { get; set; }

    [JsonProperty("restart_needed")]
    public bool RestartNeeded { get; set; }

    [JsonProperty("latest_version")]
    public string? LatestVersion { get; set; }

    [JsonProperty("setup_phase")]
    public string? SetupPhase { get; set; }

    [JsonProperty("internal_address")]
    public string? InternalAddress { get; set; }

    [JsonProperty("external_address")]
    public string? ExternalAddress { get; set; }

    [JsonProperty("connectivity")]
    public ConnectivityStatusDto? Connectivity { get; set; }

    [JsonProperty("app_status")]
    public AppProcessStatusDto? AppStatus { get; set; }
}
