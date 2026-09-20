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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Telemetry;

/// <summary>
/// What this server tells NoMercy about its plugins.
/// <para>
/// Three rules, and they are the whole of it. Refusal counts always go, so a
/// plugin refusing the same thing on a thousand servers is a plugin with a bug
/// rather than a thousand owners with a problem. Crash and ceiling counters go
/// only when the owner said yes. A sideloaded plugin is never mentioned: it is
/// somebody's own code on their own machine and none of NoMercy's business.
/// </para>
/// </summary>
public class PluginTelemetryReporter(
    IPluginManifestSource plugins,
    IPluginRefusalCounter refusals,
    IPluginCrashCounter crashes,
    IPluginInstallFacts facts,
    IPluginTelemetrySink sink,
    Func<PluginTelemetryOptions> options,
    TimeProvider clock,
    Guid serverId
)
{
    public PluginTelemetryReport Build(DateTimeOffset windowStart)
    {
        bool counters = options().ShareCounters;

        List<PluginTelemetryEntry> entries =
        [
            .. plugins
                .All()
                .Select(plugin => plugin.Id)
                .Where(pluginId => !facts.IsSideloaded(pluginId))
                .Select(pluginId => new PluginTelemetryEntry(
                    pluginId,
                    "marketplace",
                    [
                        .. refusals
                            .Counts(pluginId)
                            .Select(count => new PluginTelemetryRefusal(count.Key, count.Value)),
                    ],
                    counters ? crashes.CrashesFor(pluginId) : null,
                    counters ? crashes.CeilingHitsFor(pluginId) : null
                )),
        ];

        return new(serverId, windowStart, clock.GetUtcNow(), entries);
    }

    public async Task SendAsync(DateTimeOffset windowStart, CancellationToken ct = default)
    {
        await sink.SendAsync(Build(windowStart), ct);

        // The window is over whether or not anyone received it. Keeping the
        // counts would report the same hour again next hour.
        crashes.Reset();
    }

    /// <summary>
    /// A plugin arriving, changing or leaving. Silent for a sideload, which is
    /// the one install NoMercy never hears about.
    /// </summary>
    public Task ReportInstallAsync(
        Ulid pluginId,
        Version version,
        string kind,
        CancellationToken ct = default
    )
    {
        if (facts.IsSideloaded(pluginId))
            return Task.CompletedTask;

        return sink.SendInstallAsync(new(pluginId, version.ToString(), kind), ct);
    }
}
