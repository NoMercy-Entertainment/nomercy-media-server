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

/// <summary>
/// The <c>{ data }</c> envelope, on the plugin side of the boundary.
/// <para>
/// The server has its own <c>DataResponseDto</c> and this is deliberately not
/// it: that type lives in an assembly no plugin should be binding to. The two
/// are held identical by a serialisation test, not by sharing a type.
/// </para>
/// </summary>
public record PluginDataResponse<T>
{
    [JsonProperty("data")]
    public T? Data { get; set; }
}
