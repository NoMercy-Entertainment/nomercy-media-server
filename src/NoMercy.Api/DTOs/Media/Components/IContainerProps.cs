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
/// Props for container components that can hold child components.
/// </summary>
public interface IContainerProps : IComponentProps
{
    [JsonProperty("title")]
    string Title { get; set; }

    [JsonProperty("more_link")]
    Uri? MoreLink { get; set; }

    [JsonProperty("more_link_text")]
    string? MoreLinkText { get; }

    [JsonProperty("items")]
    IEnumerable<ComponentEnvelope> Items { get; set; }

    [JsonProperty("context_menu_items")]
    IEnumerable<ContextMenuItemDto>? ContextMenuItems { get; set; }

    [JsonProperty("url")]
    Uri? Url { get; set; }

    [JsonProperty("properties")]
    Dictionary<string, dynamic>? Properties { get; set; }
}
