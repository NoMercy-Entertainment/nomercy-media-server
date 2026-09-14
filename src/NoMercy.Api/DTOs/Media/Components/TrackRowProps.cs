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
/// Props for NMTrackRow component - single track in a list.
/// </summary>
public record TrackRowProps : LeafProps<TrackRowData>
{
    [JsonProperty("displayList", NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<TrackRowData>? DisplayList { get; set; }
}
