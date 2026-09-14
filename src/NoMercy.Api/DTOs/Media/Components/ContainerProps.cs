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
/// Base implementation for container component props.
/// Container components can hold child components.
/// </summary>
public record ContainerProps : IContainerProps
{
    [JsonProperty("id")]
    public dynamic Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("next_id", NullValueHandling = NullValueHandling.Ignore)]
    public dynamic? NextId { get; set; }

    [JsonProperty("previous_id", NullValueHandling = NullValueHandling.Ignore)]
    public dynamic? PreviousId { get; set; }

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("more_link", NullValueHandling = NullValueHandling.Ignore)]
    public Uri? MoreLink { get; set; }

    [JsonProperty("more_link_text", NullValueHandling = NullValueHandling.Ignore)]
    public string? MoreLinkText => MoreLink is not null ? "See all".Localize() : null;

    [JsonProperty("items")]
    public IEnumerable<ComponentEnvelope> Items { get; set; } = [];

    [JsonProperty("context_menu_items", NullValueHandling = NullValueHandling.Ignore)]
    public IEnumerable<ContextMenuItemDto>? ContextMenuItems { get; set; }

    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)]
    public Uri? Url { get; set; }

    [JsonProperty("properties", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, dynamic>? Properties { get; set; }
}
