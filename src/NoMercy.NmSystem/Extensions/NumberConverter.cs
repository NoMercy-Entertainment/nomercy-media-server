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

using System.Text.RegularExpressions;

namespace NoMercy.NmSystem.Extensions;

public partial class NumberConverter
{
    private static readonly Dictionary<int, string> UnitsMap = new()
    {
        { 0, "zero" },
        { 1, "one" },
        { 2, "two" },
        { 3, "three" },
        { 4, "four" },
        { 5, "five" },
        { 6, "six" },
        { 7, "seven" },
        { 8, "eight" },
        { 9, "nine" },
        { 10, "ten" },
        { 11, "eleven" },
        { 12, "twelve" },
        { 13, "thirteen" },
        { 14, "fourteen" },
        { 15, "fifteen" },
        { 16, "sixteen" },
        { 17, "seventeen" },
        { 18, "eighteen" },
        { 19, "nineteen" },
    };

    private static readonly Dictionary<int, string> TensMap = new()
    {
        { 2, "twenty" },
        { 3, "thirty" },
        { 4, "forty" },
        { 5, "fifty" },
        { 6, "sixty" },
        { 7, "seventy" },
        { 8, "eighty" },
        { 9, "ninety" },
    };

    private static string NumberToWords(int number)
    {
        return number switch
        {
            0 => "zero",
            < 0 => "minus " + NumberToWords(Math.Abs(number)),
            _ => NumberToWordsRecursive(number),
        };
    }

    private static string NumberToWordsRecursive(int number)
    {
        return number switch
        {
            < 20 => UnitsMap[number],
            < 100 => TensMap[number / 10] + (number % 10 != 0 ? " " + UnitsMap[number % 10] : ""),
            < 1000 => UnitsMap[number / 100]
                + " hundred"
                + (number % 100 != 0 ? " " + NumberToWordsRecursive(number % 100) : ""),
            < 1000000 => NumberToWordsRecursive(number / 1000)
                + " thousand"
                + (number % 1000 != 0 ? " " + NumberToWordsRecursive(number % 1000) : ""),
            < 1000000000 => NumberToWordsRecursive(number / 1000000)
                + " million"
                + (number % 1000000 != 0 ? " " + NumberToWordsRecursive(number % 1000000) : ""),
            _ => NumberToWordsRecursive(number / 1000000000)
                + " billion"
                + (
                    number % 1000000000 != 0
                        ? " " + NumberToWordsRecursive(number % 1000000000)
                        : ""
                ),
        };
    }

    public static string NormalizeAspectRatio(double width, double height)
    {
        int w = (int)Math.Round(width);
        int h = (int)Math.Round(height);
        return NormalizeAspectRatio(w, h);
    }

    public static string NormalizeAspectRatio(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return "1:1";

        int gcd = CalculateGcd(width, height);
        int normalizedWidth = width / gcd;
        int normalizedHeight = height / gcd;

        // Check for common aspect ratios and normalize them
        Dictionary<(int w, int h), string> commonRatios = new()
        {
            { (16, 9), "16:9" },
            { (4, 3), "4:3" },
            { (3, 2), "3:2" },
            { (5, 4), "5:4" },
            { (21, 9), "21:9" },
            { (32, 9), "32:9" },
            { (1, 1), "1:1" },
        };

        // Check if it matches a common ratio (with some tolerance)
        foreach (KeyValuePair<(int w, int h), string> ratio in commonRatios)
        {
            if (IsCloseToRatio(normalizedWidth, normalizedHeight, ratio.Key.w, ratio.Key.h))
                return ratio.Value;
        }

        return $"{normalizedWidth}:{normalizedHeight}";
    }

    private static bool IsCloseToRatio(int w1, int h1, int w2, int h2, double tolerance = 0.02)
    {
        double ratio1 = (double)w1 / h1;
        double ratio2 = (double)w2 / h2;
        return Math.Abs(ratio1 - ratio2) < tolerance;
    }

    private static int CalculateGcd(int a, int b)
    {
        while (b != 0)
        {
            int temp = b;
            b = a % b;
            a = temp;
        }
        return a;
    }

    internal static string ConvertNumbersInString(string input)
    {
        return MyRegex().Replace(input, match => NumberToWords(int.Parse(match.Value)));
    }

    [GeneratedRegex(@"\b\d+\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MyRegex();
}
