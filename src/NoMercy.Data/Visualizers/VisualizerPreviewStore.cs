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

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;

namespace NoMercy.Data.Visualizers;

/// <summary>The WebP previews of the pinned bundle, found by entry id and kind (static or animated).</summary>
public static class VisualizerPreviewStore
{
    public const string Static = "static";
    public const string Animated = "animated";

    private const string Prefix = "NoMercy.Data.Visualizers.Bundle.previews.";

    // One immutable snapshot, swapped whole on each seed, so a request never sees half of a reload.
    private sealed record State(
        Dictionary<Ulid, (string? Static, string? Animated)> Names,
        Func<string, byte[]?> Reader,
        ConcurrentDictionary<string, VisualizerPreview?> Cache
    );

    private static volatile State _state = new([], ReadEmbedded, new());

    public static void Load(VisualizerBundle bundle, Func<string, byte[]?>? reader = null)
    {
        Dictionary<Ulid, (string? Static, string? Animated)> names = [];
        foreach (VisualizerBundleEntry entry in bundle.Entries)
        {
            if (entry.PreviewStatic is null && entry.PreviewAnimated is null)
                continue;

            names[VisualizerSlug.ToId(entry.Slug)] = (entry.PreviewStatic, entry.PreviewAnimated);
        }

        _state = new(names, reader ?? ReadEmbedded, new());
    }

    public static bool Has(Ulid id, string kind) => NameOf(_state, id, kind) is not null;

    public static VisualizerPreview? Find(Ulid id, string kind)
    {
        State state = _state;
        string? name = NameOf(state, id, kind);
        if (name is null)
            return null;

        return state.Cache.GetOrAdd(
            name,
            n =>
            {
                byte[]? bytes = state.Reader(n);
                return bytes is null
                    ? null
                    : new(bytes, "image/webp", Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        );
    }

    private static string? NameOf(State state, Ulid id, string kind) =>
        state.Names.TryGetValue(id, out (string? Static, string? Animated) names)
            ? kind switch
            {
                Static => names.Static,
                Animated => names.Animated,
                _ => null,
            }
            : null;

    private static byte[]? ReadEmbedded(string name)
    {
        using Stream? stream = typeof(VisualizerPreviewStore)
            .Assembly.GetManifestResourceStream(Prefix + name);
        if (stream is null)
            return null;

        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
