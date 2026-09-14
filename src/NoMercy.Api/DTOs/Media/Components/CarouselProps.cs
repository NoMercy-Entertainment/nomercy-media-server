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
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Props for NMCarousel component - displays items in a horizontal scrollable carousel.
/// </summary>
public record CarouselProps : ContainerProps
{
    [JsonProperty("auto_scroll", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AutoScroll { get; set; }

    [JsonProperty("scroll_interval", NullValueHandling = NullValueHandling.Ignore)]
    public int? ScrollInterval { get; set; }
}
