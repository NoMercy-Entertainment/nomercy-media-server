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
using Newtonsoft.Json.Linq;

namespace NoMercy.Api.DTOs.Visualizers;

public record CreateVisualizerRequest
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("author")]
    public string? Author { get; set; }

    [JsonProperty("license")]
    public string? License { get; set; }

    [JsonProperty("source_url")]
    public string? SourceUrl { get; set; }

    [JsonProperty("tags")]
    public List<string>? Tags { get; set; }

    [JsonProperty("format")]
    public string? Format { get; set; }

    [JsonProperty("payload")]
    public JToken? Payload { get; set; }
}

/// <summary>Partial update: a missing or null field is ignored.</summary>
public record UpdateVisualizerRequest
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("author")]
    public string? Author { get; set; }

    [JsonProperty("license")]
    public string? License { get; set; }

    [JsonProperty("source_url")]
    public string? SourceUrl { get; set; }

    [JsonProperty("tags")]
    public List<string>? Tags { get; set; }

    [JsonProperty("payload")]
    public JToken? Payload { get; set; }
}

public record CloneVisualizerRequest
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("author")]
    public string? Author { get; set; }
}

public record SetVisualizerEnabledRequest
{
    [JsonProperty("enabled")]
    public bool? Enabled { get; set; }
}

public record SetVisualizerChoiceRequest
{
    [JsonProperty("visualizer_id")]
    public string? VisualizerId { get; set; }
}

public record ImportDeviceStateRequest
{
    [JsonProperty("choice")]
    public string? Choice { get; set; }

    [JsonProperty("favorites")]
    public List<string>? Favorites { get; set; }

    [JsonProperty("recents")]
    public List<string>? Recents { get; set; }
}
