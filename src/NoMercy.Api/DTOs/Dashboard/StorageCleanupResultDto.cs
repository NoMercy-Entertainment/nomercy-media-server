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

public record StorageCleanupResultDto
{
    [JsonProperty("parts")]
    public List<StorageCleanupPartDto> Parts { get; init; } = [];

    [JsonProperty("total_bytes_freed")]
    public long TotalBytesFreed { get; init; }

    [JsonProperty("failed_jobs_removed")]
    public int FailedJobsRemoved { get; init; }

    public static StorageCleanupResultDto From(StorageCleanupResult result) =>
        new()
        {
            Parts = result
                .Parts.Select(part => new StorageCleanupPartDto
                {
                    Id = part.Id,
                    BytesFreed = part.BytesFreed,
                })
                .ToList(),
            TotalBytesFreed = result.TotalBytesFreed,
            FailedJobsRemoved = result.FailedJobsRemoved,
        };
}
