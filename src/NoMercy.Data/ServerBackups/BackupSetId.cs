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
/// Names of backup sets: <c>yyyyMMdd-HHmmss-fffffff</c> in UTC, down to the
/// tick, so they sort by age and an id from a request can never be a path.
/// </summary>
public static partial class BackupSetId
{
    /// <summary>Prefix of the folder a set is built in before it is published.</summary>
    public const string IncompletePrefix = ".incomplete-";

    public static string Create(DateTime utcNow) =>
        utcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);

    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9]{7}$")]
    private static partial Regex Pattern();
}
