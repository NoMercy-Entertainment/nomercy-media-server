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
/// Standard API response wrapper for component-based responses.
/// </summary>
public record ComponentResponse
{
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("data")]
    public IEnumerable<ComponentEnvelope> Data { get; set; } = [];

    public ComponentResponse() { }

    public ComponentResponse(params ComponentEnvelope[] components)
    {
        Data = components;
    }

    public ComponentResponse(IEnumerable<ComponentEnvelope> components)
    {
        Data = components;
    }

    /// <summary>
    /// Creates a response with a single component.
    /// </summary>
    public static ComponentResponse From(ComponentEnvelope component) => new(component);

    /// <summary>
    /// Creates a response with multiple components.
    /// </summary>
    public static ComponentResponse From(params ComponentEnvelope[] components) => new(components);

    /// <summary>
    /// Creates a response from a collection of components.
    /// </summary>
    public static ComponentResponse From(IEnumerable<ComponentEnvelope> components) =>
        new(components);
}
