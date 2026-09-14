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
/// Data payload for the NMEmptyState component.
/// Displayed on the home screen when there are no libraries or no scanned content.
/// </summary>
public record EmptyStateData
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonProperty("action", NullValueHandling = NullValueHandling.Ignore)]
    public EmptyStateActionData? Action { get; set; }

    [JsonProperty("auto_refresh", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AutoRefresh { get; set; }
}
