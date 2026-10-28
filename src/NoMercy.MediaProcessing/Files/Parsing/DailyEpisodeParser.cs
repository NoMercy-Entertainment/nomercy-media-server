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

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Detects the air date in a daily/dated episode file name (the scene standard
/// <c>yyyy.mm.dd</c>, also accepting <c>-</c> and <c>/</c> separators), e.g.
/// "The.Daily.Show.2024.01.15.1080p.WEB.mkv". Such episodes carry no SxxExx, so
/// the date is the only key; the resolver can later map it to the episode that
/// aired that day. Month/day are range-checked and the date must be a real
/// calendar date, so a bare year or a resolution like 1920x1080 never matches.
/// </summary>
public static partial class DailyEpisodeParser
{
    [GeneratedRegex(
        @"(?<![0-9])(?<y>(?:19|20)[0-9]{2})[._\-/](?<m>0[1-9]|1[0-2])[._\-/](?<d>0[1-9]|[12][0-9]|3[01])(?![0-9])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AirDate();

    /// <summary>
    /// Returns the air date encoded in <paramref name="name"/>, or
    /// <see langword="null"/> when there is no valid yyyy.mm.dd date.
    /// </summary>
    public static DateOnly? TryGetAirDate(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        Match match = AirDate().Match(name);
        if (!match.Success)
            return null;

        int year = int.Parse(match.Groups["y"].Value);
        int month = int.Parse(match.Groups["m"].Value);
        int day = int.Parse(match.Groups["d"].Value);

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
