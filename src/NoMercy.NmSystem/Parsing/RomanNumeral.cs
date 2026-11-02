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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Roman numerals as releases write part numbers: "Show.Name.Part.IV".
/// <para>
/// Only well-formed numerals are accepted. A run of the right letters is not a
/// numeral — "IIII", "VV" and "IC" are how a title happens to be spelled, not
/// how anyone writes four, ten or ninety-nine, and accepting them would turn
/// initials into an episode number.
/// </para>
/// </summary>
public static class RomanNumeral
{
    private const int MaxValue = 400;

    private static readonly (char Symbol, int Value)[] Symbols =
    [
        ('I', 1),
        ('V', 5),
        ('X', 10),
        ('L', 50),
        ('C', 100),
        ('D', 500),
        ('M', 1000),
    ];

    /// <summary>The value of <paramref name="raw"/>, or null when it is not a
    /// well-formed numeral in the range a part number can plausibly take.</summary>
    public static int? TryParse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        string numeral = raw.ToUpperInvariant();
        int total = 0;
        int previous = 0;

        for (int index = numeral.Length - 1; index >= 0; index--)
        {
            int value = ValueOf(numeral[index]);
            if (value == 0)
                return null;

            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        // The round trip is the well-formedness check: exactly one spelling of
        // each number is canonical, so anything that does not spell itself back
        // was never a numeral.
        return total is > 0 and <= MaxValue && ToNumeral(total) == numeral ? total : null;
    }

    private static int ValueOf(char symbol)
    {
        foreach ((char candidate, int value) in Symbols)
            if (candidate == symbol)
                return value;

        return 0;
    }

    private static string ToNumeral(int value)
    {
        (int Value, string Symbol)[] table =
        [
            (1000, "M"),
            (900, "CM"),
            (500, "D"),
            (400, "CD"),
            (100, "C"),
            (90, "XC"),
            (50, "L"),
            (40, "XL"),
            (10, "X"),
            (9, "IX"),
            (5, "V"),
            (4, "IV"),
            (1, "I"),
        ];

        System.Text.StringBuilder numeral = new();
        int remaining = value;

        foreach ((int amount, string symbol) in table)
            while (remaining >= amount)
            {
                numeral.Append(symbol);
                remaining -= amount;
            }

        return numeral.ToString();
    }
}
