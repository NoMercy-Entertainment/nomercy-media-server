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
using Newtonsoft.Json.Linq;

namespace NoMercy.Api.DTOs.Dashboard;

public record UpdateDriverRequestDto
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Optional. Allows switching driver type (e.g. local → nfs) on update.
    /// Validation runs against the new type. Existing folders attached to
    /// this driver will resolve via the new backend on next access.
    /// </summary>
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("config")]
    public JObject? Config { get; set; }

    [JsonProperty("credentials")]
    public DriverCredentialsDto? Credentials { get; set; }
}
