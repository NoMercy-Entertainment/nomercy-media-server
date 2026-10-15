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

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// One log entry, as a plugin host writes it to stdout for the server to read.
/// <para>
/// The host's stdout is a pipe the server reads into its own log. As plain
/// text the level is lost, and a message or stack trace over several lines
/// arrives as separate lines nobody can put back together. One marked JSON
/// line per entry keeps all of it. The JSON escapes every line break and
/// every non-ASCII character, so the line also survives whatever code page
/// the console runs in.
/// </para>
/// <para>
/// A line without the marker is still logged, as text: a plugin that writes
/// to the console itself is not lost.
/// </para>
/// </summary>
public sealed record PluginHostLogLine(LogLevel Level, string Message, string? Exception)
{
    public const string Marker = "nomercy:log ";

    public string Format() => Marker + JsonSerializer.Serialize(this, PluginWireJson.Options);

    /// <summary>
    /// False for a line without the marker, and for one that carries it but
    /// does not hold a whole entry. Either way the caller logs the line as
    /// text rather than dropping it.
    /// </summary>
    public static bool TryParse(string line, [NotNullWhen(true)] out PluginHostLogLine? entry)
    {
        entry = null;

        if (!line.StartsWith(Marker, StringComparison.Ordinal))
            return false;

        PluginHostLogLine? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PluginHostLogLine>(
                line.AsSpan(Marker.Length),
                PluginWireJson.Options
            );
        }
        catch (JsonException)
        {
            return false;
        }

        // A number the enum does not name, or None, is not a level anything
        // can be logged at.
        if (
            parsed?.Message is null
            || parsed.Level == LogLevel.None
            || !Enum.IsDefined(parsed.Level)
        )
            return false;

        entry = parsed;
        return true;
    }
}
