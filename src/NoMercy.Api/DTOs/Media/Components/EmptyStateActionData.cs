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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Optional call-to-action attached to an NMEmptyState component.
/// </summary>
public record EmptyStateActionData
{
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("route")]
    public string Route { get; set; } = string.Empty;
}
