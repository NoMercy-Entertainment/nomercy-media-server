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

namespace NoMercy.Data.Visualizers;

public class VisualizerBundleEntry
{
    [JsonProperty("slug")]
    public string Slug { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("author")]
    public string Author { get; set; } = "";

    [JsonProperty("license")]
    public string License { get; set; } = "";

    [JsonProperty("source_url")]
    public string? SourceUrl { get; set; }

    [JsonProperty("source_sha1")]
    public string? SourceSha1 { get; set; }

    [JsonProperty("format")]
    public string Format { get; set; } = "";

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonProperty("textures")]
    public List<string> Textures { get; set; } = [];

    [JsonProperty("renderable")]
    public VisualizerBundleRenderable Renderable { get; set; } = new();

    [JsonProperty("payload")]
    public JToken Payload { get; set; } = JValue.CreateNull();

    [JsonProperty("schema")]
    public int Schema { get; set; }

    [JsonProperty("content_hash")]
    public string ContentHash { get; set; } = "";

    [JsonProperty("payload_bytes")]
    public int PayloadBytes { get; set; }
}
