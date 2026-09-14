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
using NoMercy.MediaProcessing.Reclaim;

namespace NoMercy.Api.DTOs.Dashboard;

public sealed class ReclaimableItemDto
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; }

    [JsonProperty("mediaType")]
    public string MediaType { get; set; }

    [JsonProperty("folder")]
    public string Folder { get; set; }

    [JsonProperty("servedCopy")]
    public string ServedCopy { get; set; }

    [JsonProperty("kind")]
    public string Kind { get; set; }

    [JsonProperty("targetCount")]
    public int TargetCount { get; set; }

    [JsonProperty("reclaimableBytes")]
    public long ReclaimableBytes { get; set; }

    public ReclaimableItemDto(ReclaimableItem item)
    {
        Id = item.Id;
        Title = item.Title;
        MediaType = item.MediaType;
        Folder = item.Folder;
        ServedCopy = item.ServedCopy;
        Kind = item.Kind.ToString();
        TargetCount = item.TargetPaths.Count;
        ReclaimableBytes = item.ReclaimableBytes;
    }
}
