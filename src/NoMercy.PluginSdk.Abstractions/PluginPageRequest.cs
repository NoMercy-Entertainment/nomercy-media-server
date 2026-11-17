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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Which page of a shelf a view request asks for, read from its query string.
/// <para>
/// The cursor is the plugin's own: whatever it put in the last page's next
/// cursor comes back here unchanged. A plugin that pages by number or by
/// offset writes that number as the cursor and reads it back with
/// <see cref="Offset" />.
/// </para>
/// </summary>
public sealed record PluginPageRequest
{
    /// <summary>The first page: no cursor, the default size, nothing to append to.</summary>
    public static PluginPageRequest First { get; } = new();

    /// <summary>The cursor the plugin wrote on the previous page. Null for the first page.</summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// How many items to send, always between 1 and
    /// <see cref="PluginPaging.MaximumPageSize" />.
    /// </summary>
    public int Limit { get; init; } = PluginPaging.DefaultPageSize;

    /// <summary>
    /// The id of the shelf this page is added to. Null when the whole view is
    /// asked for. The host answers an append with that shelf alone.
    /// </summary>
    public string? Append { get; init; }

    /// <summary>Whether the client is adding to a shelf it already shows.</summary>
    public bool IsAppend => Append is not null;

    /// <summary>
    /// The cursor as a whole number of items to skip, for a plugin that pages by
    /// offset. Zero when there is no cursor or it is not a whole number.
    /// </summary>
    public int Offset =>
        int.TryParse(Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
            ? offset
            : 0;

    /// <summary>
    /// The page a view request's query asks for. A missing or unreadable limit
    /// is the default size, and a limit above the cap is the cap, so no client
    /// can make a plugin build an unbounded page.
    /// </summary>
    public static PluginPageRequest From(IReadOnlyDictionary<string, string> query)
    {
        return new()
        {
            Cursor = Value(query, PluginPaging.CursorKey),
            Limit = LimitFrom(Value(query, PluginPaging.LimitKey)),
            Append = Value(query, PluginPaging.AppendKey),
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static int LimitFrom(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
            return raw is not null && raw.All(char.IsAsciiDigit)
                // Digits too long for an int: a very large number, so the cap.
                ? PluginPaging.MaximumPageSize
                : PluginPaging.DefaultPageSize;

        if (limit < 1)
            return PluginPaging.DefaultPageSize;

        return Math.Min(limit, PluginPaging.MaximumPageSize);
    }
}
