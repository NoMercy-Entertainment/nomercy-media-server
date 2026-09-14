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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Props for NMGrid component - displays items in a grid layout.
/// </summary>
public record GridProps : ContainerProps
{
    [JsonProperty("columns", NullValueHandling = NullValueHandling.Ignore)]
    public int? Columns { get; set; }

    [JsonProperty("gap", NullValueHandling = NullValueHandling.Ignore)]
    public int? Gap { get; set; }
}
