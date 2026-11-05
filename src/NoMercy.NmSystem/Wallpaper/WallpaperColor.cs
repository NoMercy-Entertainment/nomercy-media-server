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

using System.Globalization;
using System.Text.RegularExpressions;

namespace NoMercy.NmSystem.Wallpaper;

/// <summary>
/// Turns the color a client sends into the one form every wallpaper backend
/// takes. Clients send CSS colors (the web app sends "rgb(31 123 107)"), so
/// the request boundary accepts hex and rgb()/rgba() alike.
/// </summary>
public static partial class WallpaperColor
{
    [GeneratedRegex(
        @"^rgba?\(\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*(?:[,/]\s*[\d.]+%?\s*)?\)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RgbFunction();

    [GeneratedRegex(
        "^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex HexColor();

    /// <summary>Returns "#RRGGBB", or null when the input is no color we read.</summary>
    public static string? ToHex(string? input)
    {
        string value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return null;

        Match rgb = RgbFunction().Match(value);
        if (rgb.Success)
        {
            int[] channels = new int[3];
            for (int i = 0; i < 3; i++)
            {
                channels[i] = int.Parse(rgb.Groups[i + 1].Value, CultureInfo.InvariantCulture);
                if (channels[i] > 255)
                    return null;
            }
            return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
        }

        Match hex = HexColor().Match(value);
        if (!hex.Success)
            return null;

        string digits = hex.Groups[1].Value;
        if (digits.Length == 3)
            digits = string.Concat(digits.Select(c => new string(c, 2)));

        return "#" + digits.ToUpperInvariant();
    }

    /// <summary>Windows keeps Control Panel\Colors\Background as "R G B" in decimal.</summary>
    public static string ToRegistryRgb(string hex)
    {
        string digits = hex.TrimStart('#');
        int r = Convert.ToInt32(digits[..2], 16);
        int g = Convert.ToInt32(digits[2..4], 16);
        int b = Convert.ToInt32(digits[4..6], 16);
        return $"{r} {g} {b}";
    }
}
