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
using NoMercy.Api.Services;

namespace NoMercy.Api.DTOs.Dashboard;

public record StorageReportDto
{
    [JsonProperty("parts")]
    public List<StoragePartDto> Parts { get; init; } = [];

    [JsonProperty("total_bytes")]
    public long TotalBytes { get; init; }

    [JsonProperty("free_disk_bytes")]
    public long? FreeDiskBytes { get; init; }

    public static StorageReportDto From(StorageReport report) =>
        new()
        {
            Parts = report.Parts.Select(StoragePartDto.From).ToList(),
            TotalBytes = report.TotalBytes,
            FreeDiskBytes = report.FreeDiskBytes,
        };
}
