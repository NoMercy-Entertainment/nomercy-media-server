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
/// Base implementation for leaf component props.
/// Leaf components hold data but cannot have children.
/// </summary>
/// <typeparam name="TData">The type of data this component displays.</typeparam>
public record LeafProps<TData> : ILeafProps<TData>
{
    [JsonProperty("id")]
    public dynamic Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("next_id", NullValueHandling = NullValueHandling.Ignore)]
    public dynamic? NextId { get; set; }

    [JsonProperty("previous_id", NullValueHandling = NullValueHandling.Ignore)]
    public dynamic? PreviousId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("data")]
    public TData? Data { get; set; }

    [JsonProperty("watch")]
    public bool Watch { get; set; }

    [JsonProperty("context_menu_items", NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<ContextMenuItemDto>? ContextMenuItems { get; set; }

    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
    public Uri? Url { get; set; }

    [JsonProperty("properties", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, dynamic>? Properties { get; set; }
}
