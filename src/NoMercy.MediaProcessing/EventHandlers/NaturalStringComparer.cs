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

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>Orders text so digit runs compare as numbers: E2 before E10.</summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static NaturalStringComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int i = 0;
        int j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int startX = i;
                int startY = j;
                while (i < x.Length && char.IsDigit(x[i]))
                    i++;
                while (j < y.Length && char.IsDigit(y[j]))
                    j++;

                ReadOnlySpan<char> runX = x.AsSpan(startX, i - startX).TrimStart('0');
                ReadOnlySpan<char> runY = y.AsSpan(startY, j - startY).TrimStart('0');
                if (runX.Length != runY.Length)
                    return runX.Length.CompareTo(runY.Length);

                int byDigits = runX.CompareTo(runY, StringComparison.Ordinal);
                if (byDigits != 0)
                    return byDigits;
                continue;
            }

            int byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0)
                return byChar;
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
