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

using System.Text.Json;

namespace NoMercy.PluginSdk.Revocation;

/// <summary>
/// The one JSON reader for the fetched list, the offline bundle and the copy on disk.
/// Reads listing_id/version/sha256 as published, and the earlier plugin_id/hash.
/// </summary>
internal static class PluginRevocationListReader
{
    /// <summary>Reads an object holding <c>issued_at</c> and <c>entries</c>.</summary>
    public static PluginRevocationList Read(JsonElement list) =>
        new(
            list.GetProperty("issued_at").GetDateTimeOffset(),
            [.. list.GetProperty("entries").EnumerateArray().Select(ReadEntry)]
        );

    private static PluginRevocationEntry ReadEntry(JsonElement entry)
    {
        string id =
            Text(entry, "listing_id")
            ?? Text(entry, "plugin_id")
            ?? throw new FormatException("A revocation entry names no listing.");

        return new(
            Ulid.Parse(id),
            Text(entry, "version"),
            Text(entry, "sha256") ?? Text(entry, "hash"),
            Text(entry, "reason") ?? string.Empty
        );
    }

    /// <summary>A missing, null, empty or non-text value all read as "not named".</summary>
    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
