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

using System.Collections.Concurrent;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Telemetry;

namespace NoMercy.PluginSdk.Watchdog;

/// <summary>What one plugin was using when it was last looked at.</summary>
public sealed record PluginResourceSample(double CpuPercent, long MemoryBytes);

/// <summary>The ceilings the owner allowed one plugin.</summary>
public sealed record PluginResourceCeilings(double CpuPercent, long MemoryBytes)
{
    /// <summary>What a plugin gets when the owner has not said otherwise.</summary>
    public static PluginResourceCeilings Default { get; } = new(50, 512L * 1024 * 1024);
}

public enum PluginWatchdogAction
{
    None,
    ThrottleCpu,
    Restart,
    Disable,
}

/// <summary>
/// Watches what a plugin uses and acts when it goes past what the owner
/// allowed.
/// <para>
/// This stage restarts rather than caps, and says so. A plugin runs inside the
/// server process, so there is no ceiling here that a plugin cannot walk
/// through; what there is, is a server that notices and acts rather than one
/// that slows to a stop while the owner wonders why.
/// </para>
/// <para>
/// CPU is throttled and memory restarts, because they fail differently. A
/// plugin burning CPU is still answering, so slowing it down keeps it working.
/// A plugin holding memory is not going to give it back, and waiting for it to
/// is waiting for the whole server to run out.
/// </para>
/// </summary>
public class PluginWatchdog(
    IPluginResourceCeilingSource ceilings,
    IPluginWatchdogLifecycle lifecycle,
    TimeProvider clock,
    IPluginCrashCounter? counter = null
)
{
    /// <summary>
    /// Restarting a plugin that keeps breaching is a loop, so the third one in
    /// an hour stops rather than starts it. The owner is told, because a
    /// plugin that quietly stopped is a plugin they think is broken.
    /// </summary>
    public const int RestartsBeforeDisable = 3;

    private readonly ConcurrentDictionary<Ulid, List<DateTimeOffset>> _restarts = new();
    private readonly ConcurrentDictionary<Ulid, PluginRefusal> _refusals = new();

    public PluginRefusal? LastRefusal(Ulid pluginId) =>
        _refusals.TryGetValue(pluginId, out PluginRefusal? refusal) ? refusal : null;

    public PluginWatchdogAction Observe(Ulid pluginId, PluginResourceSample sample)
    {
        PluginResourceCeilings allowed = ceilings.For(pluginId);

        if (sample.MemoryBytes > allowed.MemoryBytes)
            return OverMemory(pluginId, sample, allowed);

        if (sample.CpuPercent > allowed.CpuPercent)
        {
            Record(
                pluginId,
                PluginRefusalCodes.ResourceCeiling,
                $"{pluginId} used {sample.CpuPercent:0}% of a processor, and this server allows it {allowed.CpuPercent:0}%.",
                "The plugin is being slowed down rather than stopped, so it keeps working.",
                "Raise the plugin's processor allowance on its page, or ask the author why it is this busy.",
                PluginRefusalSeverity.Degraded
            );

            lifecycle.Throttle(pluginId);

            return PluginWatchdogAction.ThrottleCpu;
        }

        return PluginWatchdogAction.None;
    }

    private PluginWatchdogAction OverMemory(
        Ulid pluginId,
        PluginResourceSample sample,
        PluginResourceCeilings allowed
    )
    {
        DateTimeOffset now = clock.GetUtcNow();
        List<DateTimeOffset> recent = _restarts.GetOrAdd(pluginId, _ => []);

        lock (recent)
        {
            recent.RemoveAll(at => now - at >= TimeSpan.FromHours(1));

            if (recent.Count >= RestartsBeforeDisable)
            {
                Record(
                    pluginId,
                    PluginRefusalCodes.DisabledAfterRestarts,
                    $"{pluginId} was restarted {recent.Count} times in an hour and has been turned off.",
                    "A plugin that breaches again every time it comes back is in a loop, and restarting it forever is a server that never settles.",
                    "Turn it on again from its page once the author has a fix, or raise its memory allowance if the ceiling is the wrong one.",
                    PluginRefusalSeverity.Blocked
                );

                lifecycle.Disable(pluginId);

                return PluginWatchdogAction.Disable;
            }

            recent.Add(now);
        }

        Record(
            pluginId,
            PluginRefusalCodes.ResourceCeiling,
            $"{pluginId} held {sample.MemoryBytes / 1024 / 1024} MB, and this server allows it {allowed.MemoryBytes / 1024 / 1024} MB.",
            "Memory is not given back by asking, so the plugin is restarted rather than slowed down.",
            "Raise the plugin's memory allowance on its page, or ask the author why it is holding this much.",
            PluginRefusalSeverity.Degraded
        );

        lifecycle.Restart(pluginId);

        return PluginWatchdogAction.Restart;
    }

    private void Record(
        Ulid pluginId,
        string code,
        string what,
        string why,
        string fix,
        PluginRefusalSeverity severity
    )
    {
        _refusals[pluginId] = new(code, pluginId.ToString(), what, why, fix, severity);

        if (code == PluginRefusalCodes.ResourceCeiling)
            counter?.RecordCeilingHit(pluginId);
    }
}

/// <summary>Where the ceilings for one plugin come from.</summary>
public interface IPluginResourceCeilingSource
{
    PluginResourceCeilings For(Ulid pluginId);
}

/// <summary>
/// What the watchdog can do to a plugin. Narrow on purpose: the thing that
/// decides a plugin is misbehaving should not also be able to install one.
/// </summary>
public interface IPluginWatchdogLifecycle
{
    void Throttle(Ulid pluginId);

    void Restart(Ulid pluginId);

    void Disable(Ulid pluginId);
}
