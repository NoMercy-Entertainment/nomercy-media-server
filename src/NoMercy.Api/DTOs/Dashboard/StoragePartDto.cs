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

public record StoragePartDto
{
    [JsonProperty("id")]
    public string Id { get; init; } = string.Empty;

    [JsonProperty("group")]
    public string Group { get; init; } = string.Empty;

    [JsonProperty("bytes")]
    public long Bytes { get; init; }

    [JsonProperty("reclaimable_bytes")]
    public long ReclaimableBytes { get; init; }

    public static StoragePartDto From(StoragePart part) =>
        new()
        {
            Id = part.Id,
            Group = part.Group,
            Bytes = part.Bytes,
            ReclaimableBytes = part.ReclaimableBytes,
        };
}
