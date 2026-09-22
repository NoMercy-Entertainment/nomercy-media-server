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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>How a table cell renders its value.</summary>
public static class PluginTableCellType
{
    public const string Text = "text";

    /// <summary>A number between 0 and 1, drawn as a bar.</summary>
    public const string Progress = "progress";

    /// <summary>A string paired with a <see cref="PluginBadgeVariant"/>.</summary>
    public const string Badge = "badge";

    /// <summary>A byte count, formatted by the client in the user's locale.</summary>
    public const string Bytes = "bytes";

    /// <summary>A byte-per-second rate, formatted by the client.</summary>
    public const string Rate = "rate";

    /// <summary>A duration in seconds, formatted by the client.</summary>
    public const string Duration = "duration";

    /// <summary>
    /// A list of <see cref="PluginTableAction"/>, drawn as buttons in the cell.
    /// A row carries one action - the row itself - so a row that needs both a
    /// pause and a destructive cancel had nowhere to put the second one.
    /// </summary>
    public const string Actions = "actions";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Text,
            Progress,
            Badge,
            Bytes,
            Rate,
            Duration,
            Actions,
        };
}
