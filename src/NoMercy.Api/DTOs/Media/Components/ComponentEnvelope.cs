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
/// The envelope that wraps all components. This is the main type sent over the wire.
/// Uses discriminated union pattern where 'component' field determines the props type.
/// </summary>
public record ComponentEnvelope
{
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("component")]
    public string Component { get; set; } = string.Empty;

    [JsonProperty("props")]
    public object Props { get; set; } = new();

    [JsonProperty("update", NullValueHandling = NullValueHandling.Ignore)]
    public UpdateDto? Update { get; set; }

    [JsonProperty("replacing", NullValueHandling = NullValueHandling.Ignore)]
    public Ulid? Replacing { get; set; }
}
