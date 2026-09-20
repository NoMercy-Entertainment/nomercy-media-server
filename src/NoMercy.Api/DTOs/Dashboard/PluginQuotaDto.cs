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
/// What one plugin is allowed, as the owner set it.
/// <para>
/// Every number is what the plugin may use, not what it may not reach. A
/// number at or below zero is read as unlimited, and the answer carries a
/// warning saying what the owner has just turned off.
/// </para>
/// </summary>
public record PluginQuotaDto
{
    [JsonProperty("cpuPercent")]
    public double CpuPercent { get; init; }

    [JsonProperty("memoryBytes")]
    public long MemoryBytes { get; init; }

    [JsonProperty("diskBytes")]
    public long DiskBytes { get; init; }

    [JsonProperty("uploadBytesPerSecond")]
    public long UploadBytesPerSecond { get; init; }

    /// <summary>What the owner gave up by raising something to unlimited. Null when nothing is.</summary>
    [JsonProperty("warning")]
    public string? Warning { get; init; }

    /// <summary>How much disk the plugin is holding right now, so the number above has a scale.</summary>
    [JsonProperty("diskUsedBytes")]
    public long DiskUsedBytes { get; init; }
}
