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

using Mono.Nat;
using Newtonsoft.Json;
using NoMercy.Providers.Helpers;

namespace NoMercy.Api.DTOs.Common;

public class Heatmap
{
    [JsonProperty("start_time")]
    public double StartTime { get; set; }

    [JsonProperty("end_time")]
    public double EndTime { get; set; }

    [JsonProperty("value")]
    public double Value { get; set; }
}
