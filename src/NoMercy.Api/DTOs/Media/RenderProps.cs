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
using NoMercy.Api.DTOs.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media;

public record RenderProps<T>
{
    [JsonProperty("id")]
    public dynamic Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("next_id")]
    public dynamic NextId { get; set; } = Ulid.NewUlid();

    [JsonProperty("previous_id")]
    public dynamic PreviousId { get; set; } = Ulid.NewUlid();

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("more_link")]
    public Uri? MoreLink { get; set; }

    [JsonProperty("more_link_text")]
    public string? MoreText => MoreLink is not null ? "See all".Localize() : null;

    [JsonProperty("items")]
    public IEnumerable<ComponentDto<T>>? Items { get; set; } = [];

    [JsonProperty("data")]
    public T? Data { get; set; }

    [JsonProperty("watch")]
    public bool Watch { get; set; }

    [JsonProperty("context_menu_items")]
    public Dictionary<string, object>[]? ContextMenuItems { get; set; } = [];

    [JsonProperty("url")]
    public Uri? Url { get; set; }

    [JsonProperty("displayList")]
    public IEnumerable<ArtistTrackDto>? DisplayList { get; set; } = [];

    [JsonProperty("properties")]
    public Dictionary<string, dynamic> Properties { get; set; } = new();
}
