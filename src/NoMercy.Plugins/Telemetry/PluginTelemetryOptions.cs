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
using NoMercy.NmSystem.Information;

namespace NoMercy.Plugins.Telemetry;

/// <summary>
/// What the owner agreed to send beyond the refusal counts.
/// <para>
/// Off until they say yes, and off again the moment they say no. A default of
/// on would be a decision made for somebody by whoever wrote this file.
/// </para>
/// </summary>
/// <param name="ShareCounters">Crash and resource counters.</param>
public record PluginTelemetryOptions(bool ShareCounters)
{
    public static PluginTelemetryOptions Off { get; } = new(false);

    private static string FileFor(string folder) => Path.Combine(folder, "telemetry.json");

    public static PluginTelemetryOptions Load(string? folder = null)
    {
        string file = FileFor(folder ?? AppFiles.PluginConfigPath);

        if (!File.Exists(file))
            return Off;

        try
        {
            return JsonSerializer.Deserialize<PluginTelemetryOptions>(File.ReadAllText(file))
                ?? Off;
        }
        catch (JsonException)
        {
            // A file nobody can read is not consent. Reading it as a yes would
            // send counters the owner may never have agreed to.
            return Off;
        }
    }

    public void Save(string? folder = null)
    {
        string target = folder ?? AppFiles.PluginConfigPath;

        Directory.CreateDirectory(target);
        File.WriteAllText(FileFor(target), JsonSerializer.Serialize(this));
    }
}
