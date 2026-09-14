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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Status;
using NoMercy.Setup.Boot;

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
