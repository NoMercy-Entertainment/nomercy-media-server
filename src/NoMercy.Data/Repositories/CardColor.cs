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
using NoMercy.Database;

namespace NoMercy.Data.Repositories;

/// <summary>
/// The one color a card takes from a palette, picked the way the web app's
/// pickPaletteColor does (app-web src/lib/colorHelper.ts): the first palette
/// key, in its order, whose luminosity is neither too dark nor too light.
/// Picking it here keeps a page of ~1600 posters from carrying six colors each
/// when the card reads one.
/// </summary>
public static partial class CardColor
{
    private const double TooDarkBelow = 40;
    private const double TooLightAbove = 120;

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    public static string? Pick(PaletteColors? palette)
    {
        if (palette is null || string.IsNullOrEmpty(palette.DarkVibrant))
            return null;

        string?[] candidates =
        [
            palette.DarkVibrant,
            palette.Primary,
            palette.Dominant,
            palette.LightVibrant,
            palette.DarkMuted,
            palette.LightMuted,
        ];

        return candidates.FirstOrDefault(color => InRange(color));
    }

    private static bool InRange(string? color)
    {
        if (string.IsNullOrEmpty(color))
            return false;

        double luminosity = Luminosity(color);
        return luminosity >= TooDarkBelow && luminosity <= TooLightAbove;
    }

    // Same arithmetic and order as the web helper, so an edge value rounds the same way.
    private static double Luminosity(string color)
    {
        int r;
        int g;
        int b;

        if (color[0] == '#')
        {
            string hex =
                color.Length == 4
                    ? $"{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}"
                    : color.Substring(1, Math.Min(6, color.Length - 1));
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n))
                return 0;
            r = (n >> 16) & 0xFF;
            g = (n >> 8) & 0xFF;
            b = n & 0xFF;
        }
        else
        {
            MatchCollection numbers = Number().Matches(color);
            if (numbers.Count < 3)
                return 0;
            r = int.Parse(numbers[0].Value, CultureInfo.InvariantCulture);
            g = int.Parse(numbers[1].Value, CultureInfo.InvariantCulture);
            b = int.Parse(numbers[2].Value, CultureInfo.InvariantCulture);
        }

        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }
}
