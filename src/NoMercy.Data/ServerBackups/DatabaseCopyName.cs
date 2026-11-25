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

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// The name of a copy of one database at the top of the backups folder:
/// <c>media.20261117040000.db</c>, the database's name and a UTC time to the
/// second.
/// <para>
/// The copy taken before a migration has this name. So did the daily backup
/// before it became a set, and a server that was updated across that change
/// still holds those.
/// </para>
/// </summary>
public static partial class DatabaseCopyName
{
    private const string StampFormat = "yyyyMMddHHmmss";

    public static string Create(string databaseName, DateTime utcNow) =>
        $"{databaseName}.{utcNow.ToString(StampFormat, CultureInfo.InvariantCulture)}.db";

    /// <summary>
    /// Reads a copy's name, or the name of one of the files SQLite keeps
    /// beside it (<c>-wal</c>, <c>-shm</c>, <c>-journal</c>).
    /// </summary>
    /// <param name="stamp">The fourteen digits of the copy's time.</param>
    public static bool TryParse(string? fileName, out string databaseName, out string stamp)
    {
        Match match = Pattern().Match(fileName ?? string.Empty);
        databaseName = match.Groups["database"].Value;
        stamp = match.Groups["stamp"].Value;

        return match.Success;
    }

    /// <summary>A hand-made copy beside the live databases, such as <c>media.db.bak-tag-1</c>.</summary>
    public static bool TryParseHandMade(string? fileName, out string databaseName)
    {
        Match match = HandMadePattern().Match(fileName ?? string.Empty);
        databaseName = match.Groups["database"].Value;

        return match.Success;
    }

    /// <summary>
    /// The time of a set, from its id, in the digits a copy's name carries, so
    /// the two compare as text.
    /// </summary>
    public static string StampOfSet(string setId) =>
        string.Concat(setId.AsSpan(0, 8), setId.AsSpan(9, 6));

    // \z, not $: "$" also matches before a trailing newline.
    [GeneratedRegex(
        @"\A(?<database>.+)\.(?<stamp>[0-9]{14})\.db(-wal|-shm|-journal)?\z",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex Pattern();

    [GeneratedRegex(
        @"\A(?<database>[^.]+)\.db\.bak-.+\z",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex HandMadePattern();
}
