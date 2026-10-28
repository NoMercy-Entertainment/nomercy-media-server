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

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Media;

/// <summary>
/// One music visualizer: a shader, a KodeLife project or a MilkDrop preset.
/// The payload is canonical JSON text; the server never runs it.
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(Slug), IsUnique = true)]
[Index(nameof(IsBuiltIn))]
[Index(nameof(Format))]
public class Visualizer : Timestamps
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    /// <summary>Stable key: "milkdrop/&lt;id&gt;" or "shader/&lt;id&gt;".</summary>
    [JsonProperty("slug")]
    [MaxLength(160)]
    public required string Slug { get; set; }

    [JsonProperty("name")]
    [MaxLength(256)]
    public required string Name { get; set; }

    [JsonProperty("author")]
    [MaxLength(256)]
    public required string Author { get; set; }

    [JsonProperty("license")]
    [MaxLength(256)]
    public required string License { get; set; }

    [JsonProperty("source_url")]
    [MaxLength(2048)]
    public string? SourceUrl { get; set; }

    [JsonProperty("source_sha1")]
    [MaxLength(40)]
    public string? SourceSha1 { get; set; }

    [JsonProperty("format")]
    [MaxLength(32)]
    public required string Format { get; set; }

    /// <summary>Comma-separated tags; a tag never holds a comma.</summary>
    [JsonProperty("tags")]
    [MaxLength(2560)]
    public string Tags { get; set; } = "";

    /// <summary>Comma-separated texture names a MilkDrop payload samples.</summary>
    [JsonProperty("textures")]
    [MaxLength(640)]
    public string Textures { get; set; } = "";

    [JsonProperty("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>SHA-256 (lowercase hex) of the canonical payload text.</summary>
    [JsonProperty("content_hash")]
    [MaxLength(64)]
    public required string ContentHash { get; set; }

    [JsonProperty("payload_bytes")]
    public int PayloadBytes { get; set; }

    [JsonProperty("renderable_webgl2")]
    public bool RenderableWebgl2 { get; set; } = true;

    [JsonProperty("is_built_in")]
    public bool IsBuiltIn { get; set; }

    /// <summary>"db" for user entries, "builtin" for shipped ones, "retired-builtin" for kept leftovers.</summary>
    [JsonProperty("source")]
    [MaxLength(64)]
    public string Source { get; set; } = "db";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Canonical JSON text of the payload.</summary>
    [JsonIgnore]
    [MaxLength(524288)]
    public required string PayloadJson { get; set; }

    [JsonIgnore]
    public ICollection<VisualizerUser> Users { get; set; } = [];
}
