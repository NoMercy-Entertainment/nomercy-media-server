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

namespace NoMercy.PluginSdk.Mvc;

/// <summary>The <c>{ status, data, message, args }</c> envelope.</summary>
public record PluginStatusResponse<T>
{
    [JsonProperty("status")]
    public string Status { get; set; } = "ok";

    [JsonProperty("data")]
    public T Data { get; set; } = default!;

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("args")]
    public object[]? Args { get; set; } = [];
}
