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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>What the owner is consenting to, in one call.</summary>
public class PluginConsentRequestDto
{
    [JsonProperty("grants")]
    public List<PluginGrantDto> Grants { get; set; } = [];
}
