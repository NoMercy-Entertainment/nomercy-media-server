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

using System.IO.Compression;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NoMercy.Data.Visualizers;

/// <summary>The pinned visualizer bundle released by the content repo, read from the embedded copy.</summary>
public class VisualizerBundle
{
    private const string ResourceName =
        "NoMercy.Data.Visualizers.Bundle.visualizers-bundle.json.gz";
    private const string HashResourceName = "NoMercy.Data.Visualizers.Bundle.bundle-hash.txt";

    [JsonProperty("bundle_schema")]
    public int BundleSchema { get; set; }

    [JsonProperty("bundle_hash")]
    public string BundleHash { get; set; } = "";

    [JsonProperty("entries")]
    public List<VisualizerBundleEntry> Entries { get; set; } = [];

    [JsonProperty("textures")]
    public List<VisualizerBundleTexture> Textures { get; set; } = [];

    public static VisualizerBundle LoadEmbedded()
    {
        Assembly assembly = typeof(VisualizerBundle).Assembly;
        using Stream stream =
            assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The visualizer bundle is not embedded.");
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        using JsonTextReader json = new(reader) { MaxDepth = 64 };
        return new JsonSerializer().Deserialize<VisualizerBundle>(json)
            ?? throw new InvalidOperationException("The visualizer bundle is empty.");
    }

    /// <summary>The bundle_hash the update script recorded when it pinned this copy.</summary>
    public static string PinnedHash()
    {
        using Stream stream =
            typeof(VisualizerBundle).Assembly.GetManifestResourceStream(HashResourceName)
            ?? throw new InvalidOperationException("The visualizer bundle pin is not embedded.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd().Trim();
    }

    /// <summary>Hash of the entries as the content repo computes it, without the preview_* keys.</summary>
    public string ComputeHash()
    {
        JArray entries = JArray.FromObject(Entries);
        foreach (JObject entry in entries.Cast<JObject>())
        {
            entry.Remove("preview_static");
            entry.Remove("preview_animated");
            entry.Remove("preview_placeholder");
        }

        return VisualizerCanonicalJson.Hash(VisualizerCanonicalJson.Serialize(entries));
    }
}
