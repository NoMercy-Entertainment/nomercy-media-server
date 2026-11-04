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

using System.Text;
using Newtonsoft.Json;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Output;
using NoMercy.Storage;

namespace NoMercy.Encoder.Bundle;

/// <inheritdoc cref="IMediaBlueprintWriter"/>
public class MediaBlueprintWriter(IMediaBlueprintBuilder builder) : IMediaBlueprintWriter
{
    /// <summary>The single per-media-item blueprint file, at the media folder root.</summary>
    public const string FileName = ".nomercy.json";

    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        Formatting = Formatting.Indented,
        DateParseHandling = DateParseHandling.None,
    };

    public async Task WriteAsync(
        IStorage storage,
        string mediaRootPath,
        MediaInfo source,
        BlueprintIdentity identity,
        OutputPlan plan,
        BundleLayout layout,
        IReadOnlyList<string> outputFiles,
        string outputLocation,
        string encoderVersion,
        string? profileFingerprint,
        DateTime createdAt,
        DateTime completedAt,
        CancellationToken ct,
        string? originalSourcePath = null
    )
    {
        BlueprintEncode encode = builder.BuildEncode(
            source,
            plan,
            layout,
            outputFiles,
            outputLocation,
            encoderVersion,
            profileFingerprint,
            createdAt,
            completedAt
        );

        MediaBlueprint? existing = await ReadAsync(storage, mediaRootPath, ct);
        MediaBlueprint blueprint = existing is null
            ? builder.BuildFromSource(source, identity, originalSourcePath) with
            {
                Encodes = [encode],
            }
            : existing with
            {
                Encodes = MergeEncode(existing.Encodes, encode),
            };

        string path = storage.CombinePath(mediaRootPath, FileName);
        string json = JsonConvert.SerializeObject(blueprint, SerializerSettings);
        await storage.WriteAsync(path, Encoding.UTF8.GetBytes(json), ct);
    }

    public async Task<MediaBlueprint?> ReadAsync(
        IStorage storage,
        string mediaRootPath,
        CancellationToken ct
    )
    {
        string path = storage.CombinePath(mediaRootPath, FileName);
        if (!storage.Exists(path))
            return null;

        byte[] bytes = await storage.ReadAsync(path, ct);
        string json = Encoding.UTF8.GetString(bytes);
        return JsonConvert.DeserializeObject<MediaBlueprint>(json, SerializerSettings);
    }

    /// <summary>
    /// Appends <paramref name="encode"/>, replacing any existing entry with
    /// the same <c>preset_slug</c> — a re-dispatch of the same preset
    /// overwrites its own history instead of accumulating duplicates.
    /// </summary>
    private static List<BlueprintEncode> MergeEncode(
        IReadOnlyList<BlueprintEncode> existing,
        BlueprintEncode encode
    )
    {
        List<BlueprintEncode> merged =
        [
            .. existing.Where(e =>
                !string.Equals(e.PresetSlug, encode.PresetSlug, StringComparison.OrdinalIgnoreCase)
            ),
            encode,
        ];
        return merged;
    }
}
