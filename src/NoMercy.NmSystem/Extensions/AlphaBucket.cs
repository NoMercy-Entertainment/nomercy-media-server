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

namespace NoMercy.NmSystem.Extensions;

/// <summary>
/// A-Z paging buckets for title lists. Buckets are derived from the normalized
/// <c>TitleSort</c> value (e.g. "【Oshi No Ko】" -> "oshi.no.ko" -> bucket "O"),
/// never the raw display title, so titles starting with articles or special
/// characters land in a stable, predictable bucket. "#" catches anything whose
/// TitleSort does not start with an A-Z letter: digits, punctuation, accented
/// Latin letters such as "é" and non-Latin scripts. Both <see cref="Matches"/> and
/// <see cref="LetterFor"/> apply that one rule, so every title lands in exactly one bucket.
/// </summary>
public static class AlphaBucket
{
    public static readonly string[] Buckets =
    [
        "#",
        "A",
        "B",
        "C",
        "D",
        "E",
        "F",
        "G",
        "H",
        "I",
        "J",
        "K",
        "L",
        "M",
        "N",
        "O",
        "P",
        "Q",
        "R",
        "S",
        "T",
        "U",
        "V",
        "W",
        "X",
        "Y",
        "Z",
    ];

    /// <summary>
    /// True when <paramref name="titleSort"/> belongs in <paramref name="bucket"/>.
    /// Pass the normalized TitleSort, not the raw Title.
    /// </summary>
    public static bool Matches(string? titleSort, string bucket)
    {
        return string.Equals(LetterFor(titleSort), bucket, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bucket a display name is listed under: its first letter when that is A-Z,
    /// otherwise "#".
    /// </summary>
    public static string LetterFor(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "#";

        char first = char.ToLowerInvariant(name[0]);
        return first is >= 'a' and <= 'z' ? first.ToString().ToUpperInvariant() : "#";
    }
}
