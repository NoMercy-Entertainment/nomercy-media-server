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

namespace NoMercy.Api.Controllers;

public record ComponentStatus
{
    [JsonProperty("database")]
    public required string Database { get; init; }

    [JsonProperty("authentication")]
    public required string Authentication { get; init; }

    [JsonProperty("network")]
    public required string Network { get; init; }

    [JsonProperty("registration")]
    public required string Registration { get; init; }
}
