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

/// <summary>The MilkDrop textures shipped in the pinned bundle, looked up by name without extension.</summary>
public static class VisualizerTextureStore
{
    private const string Prefix = "NoMercy.Data.Visualizers.Bundle.textures.";
    private const string Extension = ".jpg";

    private static readonly ConcurrentDictionary<string, VisualizerTexture?> Cache = new();

    public static VisualizerTexture? Find(string name)
    {
        if (!IsTextureName(name))
            return null;

        return Cache.GetOrAdd(name, Load);
    }

    public static bool IsTextureName(string name)
    {
        if (name.Length is < 1 or > 64)
            return false;

        foreach (char c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }

    private static VisualizerTexture? Load(string name)
    {
        Assembly assembly = typeof(VisualizerTextureStore).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(Prefix + name + Extension);
        if (stream is null)
            return null;

        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] bytes = copy.ToArray();
        return new(bytes, "image/jpeg", Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
