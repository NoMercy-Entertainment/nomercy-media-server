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

namespace NoMercy.Plugins.Telemetry;

/// <summary>Where a report goes. The seam that keeps the rules testable without a network.</summary>
public interface IPluginTelemetrySink
{
    Task SendAsync(PluginTelemetryReport report, CancellationToken ct = default);

    Task SendInstallAsync(PluginInstallReport report, CancellationToken ct = default);
}

/// <summary>How often a plugin crashed, and how often it went past a ceiling.</summary>
public interface IPluginCrashCounter
{
    void RecordCrash(Ulid pluginId);

    void RecordCeilingHit(Ulid pluginId);

    int CrashesFor(Ulid pluginId);

    int CeilingHitsFor(Ulid pluginId);

    /// <summary>Starts the next window. A count that never resets is a total, not a window.</summary>
    void Reset();
}
