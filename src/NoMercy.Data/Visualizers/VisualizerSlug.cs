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

using System.Security.Cryptography;
using System.Text;

namespace NoMercy.Data.Visualizers;

public static class VisualizerSlug
{
    private const int MaxNameLength = 100;

    public static readonly string[] Formats =
    [
        "twigl-classic",
        "twigl-geek",
        "twigl-geeker",
        "twigl-geekest",
        "shadertoy",
        "kodelife",
        "milkdrop",
    ];

    /// <summary>First 16 bytes of SHA-256 of the slug, the same method as the built-in encoder presets.</summary>
    public static Ulid ToId(string slug)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        return new(hash.AsSpan(0, 16).ToArray());
    }

    public static string Family(string format) => format == "milkdrop" ? "milkdrop" : "shader";

    /// <summary>A unique-looking slug for a user entry: family, name in the slug alphabet, short suffix.</summary>
    public static string Create(string format, string name)
    {
        StringBuilder body = new();
        bool lastDash = true;
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                body.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                body.Append('-');
                lastDash = true;
            }

            if (body.Length >= MaxNameLength)
                break;
        }

        string text = body.ToString().Trim('-');
        if (text.Length == 0)
            text = "visualizer";

        string suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{Family(format)}/{text}-{suffix}";
    }
}
