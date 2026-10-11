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
/// Props for NMLolomo component - a page of NMCarousel rows for the TV app.
/// </summary>
public record LolomoProps
{
    public const string HeroFocus = "focus";
    public const string HeroNone = "none";

    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"focus" (the hero shows the focused card) or "none".</summary>
    [JsonProperty("hero")]
    public string Hero { get; set; } = HeroFocus;

    [JsonProperty("items")]
    public IEnumerable<ComponentEnvelope> Items { get; set; } = [];
}
