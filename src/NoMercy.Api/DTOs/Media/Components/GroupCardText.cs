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
/// A card text as an i18n key plus its values. The client translates it with
/// its own tables, so plugins can add keys the same way.
/// </summary>
public record GroupCardText(
    [property: JsonProperty("key")] string Key,
    [property: JsonProperty("values")] Dictionary<string, object> Values
)
{
    public static GroupCardText Of(string key) => new(key, []);
}
