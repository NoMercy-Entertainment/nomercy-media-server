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
using NoMercy.Database.Models.Media;

namespace NoMercy.Api.DTOs.Visualizers;

public record VisualizerRenderableDto
{
    [JsonProperty("webgl2")]
    public bool Webgl2 { get; set; }
}

/// <summary>Content fields, identical for every user, so safe under one ETag.</summary>
public record VisualizerCoreDto
{
    [JsonProperty("id")]
    public Ulid Id { get; set; }

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
    public string[] Tags { get; set; } = [];

    [JsonProperty("schema")]
    public int Schema { get; set; }

    [JsonProperty("content_hash")]
    public string ContentHash { get; set; } = "";

    [JsonProperty("payload_bytes")]
    public int PayloadBytes { get; set; }

    [JsonProperty("textures")]
    public string[] Textures { get; set; } = [];

    [JsonProperty("renderable")]
    public VisualizerRenderableDto Renderable { get; set; } = new();

    [JsonProperty("is_built_in")]
    public bool IsBuiltIn { get; set; }

    [JsonProperty("source")]
    public string Source { get; set; } = "";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; }

    protected void Fill(Visualizer entity)
    {
        Id = entity.Id;
        Slug = entity.Slug;
        Name = entity.Name;
        Author = entity.Author;
        License = entity.License;
        SourceUrl = entity.SourceUrl;
        SourceSha1 = entity.SourceSha1;
        Format = entity.Format;
        Tags = Split(entity.Tags);
        Schema = entity.Schema;
        ContentHash = entity.ContentHash;
        PayloadBytes = entity.PayloadBytes;
        Textures = Split(entity.Textures);
        Renderable = new() { Webgl2 = entity.RenderableWebgl2 };
        IsBuiltIn = entity.IsBuiltIn;
        Source = entity.Source;
        Enabled = entity.Enabled;
        CreatedAt = DateTime.SpecifyKind(entity.CreatedAt, DateTimeKind.Utc);
        UpdatedAt = DateTime.SpecifyKind(entity.UpdatedAt, DateTimeKind.Utc);
    }

    private static string[] Split(string value) =>
        value.Length == 0 ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>One row of the list: the core plus this user's flags.</summary>
public record VisualizerSummaryDto : VisualizerCoreDto
{
    [JsonProperty("is_favorite")]
    public bool IsFavorite { get; set; }

    [JsonProperty("last_used_at")]
    public DateTime? LastUsedAt { get; set; }

    public static VisualizerSummaryDto From(Visualizer entity, VisualizerUser? link)
    {
        VisualizerSummaryDto dto = new()
        {
            IsFavorite = link?.IsFavorite ?? false,
            LastUsedAt = link?.LastUsedAt is { } used
                ? DateTime.SpecifyKind(used, DateTimeKind.Utc)
                : null,
        };
        dto.Fill(entity);
        return dto;
    }
}

/// <summary>Detail: the core plus the payload, with no per-user fields so the ETag stays true.</summary>
public record VisualizerDto : VisualizerCoreDto
{
    [JsonProperty("payload")]
    public JToken Payload { get; set; } = new JObject();

    public static VisualizerDto From(Visualizer entity)
    {
        VisualizerDto dto = new() { Payload = JToken.Parse(entity.PayloadJson) };
        dto.Fill(entity);
        return dto;
    }
}

public record VisualizerChoiceDto
{
    [JsonProperty("visualizer_id")]
    public string? VisualizerId { get; set; }

    [JsonProperty("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}

public record ImportDeviceStateResponse
{
    [JsonProperty("choice_applied")]
    public bool ChoiceApplied { get; set; }

    [JsonProperty("favorites_added")]
    public int FavoritesAdded { get; set; }

    [JsonProperty("recents_added")]
    public int RecentsAdded { get; set; }

    [JsonProperty("skipped")]
    public List<ImportSkippedDto> Skipped { get; set; } = [];
}

public record ImportSkippedDto
{
    [JsonProperty("value")]
    public string Value { get; set; } = "";

    [JsonProperty("reason")]
    public string Reason { get; set; } = "";
}
