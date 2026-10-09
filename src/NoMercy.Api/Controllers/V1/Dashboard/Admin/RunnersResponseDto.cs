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
using NoMercy.Api.DTOs.Common;

namespace NoMercy.Api.Controllers.V1.Dashboard.Admin;

/// <summary>
/// Active task-worker count for the dashboard's pause toggle. Read directly
/// off <c>response.data</c> by the client, not through a
/// <see cref="DataResponseDto{T}"/> wrapper.
/// </summary>
public class RunnersResponseDto
{
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    [JsonProperty("workers")]
    public int Workers { get; set; }
}
