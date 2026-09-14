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
/// Props for leaf components that hold data but cannot have children.
/// </summary>
/// <typeparam name="TData">The type of data this component displays.</typeparam>
public interface ILeafProps<TData> : IComponentProps
{
    [JsonProperty("title")]
    string Title { get; set; }

    [JsonProperty("data")]
    TData? Data { get; set; }

    [JsonProperty("watch")]
    bool Watch { get; set; }

    [JsonProperty("context_menu_items")]
    IEnumerable<ContextMenuItemDto>? ContextMenuItems { get; set; }

    [JsonProperty("url")]
    Uri? Url { get; set; }

    [JsonProperty("properties")]
    Dictionary<string, dynamic>? Properties { get; set; }
}
