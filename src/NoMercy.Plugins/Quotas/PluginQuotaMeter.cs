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
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Quotas;

/// <summary>Where one plugin's quota comes from.</summary>
public interface IPluginQuotaSource
{
    PluginQuota For(Ulid pluginId);
}

/// <summary>
/// Counts what a plugin has spent and refuses when it runs out.
/// <para>
/// Measured at the facades rather than guessed from the outside: the server
/// hands over every byte a plugin writes and every byte it proxies, so those
/// are the two places where a number is a fact rather than an estimate.
/// </para>
/// </summary>
public class PluginQuotaMeter(
    IPluginQuotaSource quotas,
    TimeProvider clock,
    IPluginRefusalCounter? counter = null
)
{
    private readonly ConcurrentDictionary<Ulid, long> _disk = new();
    private readonly ConcurrentDictionary<Ulid, (DateTimeOffset Second, long Bytes)> _upload =
        new();

    /// <summary>How much disk this plugin is holding right now.</summary>
    public long DiskUsed(Ulid pluginId) => _disk.TryGetValue(pluginId, out long used) ? used : 0;

    /// <summary>
    /// Null when the write may happen. Refused before the bytes land, because
    /// a quota checked afterwards is a quota that has already been passed.
    /// </summary>
    public PluginRefusal? AccountDisk(Ulid pluginId, long bytes)
    {
        PluginQuota quota = quotas.For(pluginId);
        long after = DiskUsed(pluginId) + bytes;

        if (after > quota.DiskBytes)
            return Counted(
                pluginId,
                new(
                    PluginRefusalCodes.QuotaDiskExceeded,
                    pluginId.ToString(),
                    $"The plugin has used all {quota.DiskBytes / 1024 / 1024} MB of the disk this server allows it.",
                    "Its next write would go past that, so the server refused it rather than filling the disk.",
                    "Raise the plugin's disk allowance on its page, or remove what it no longer needs.",
                    PluginRefusalSeverity.Blocked
                )
            );

        _disk.AddOrUpdate(pluginId, bytes, (_, used) => used + bytes);

        return null;
    }

    /// <summary>Bytes a plugin gave back, so a delete makes room for a write.</summary>
    public void ReleaseDisk(Ulid pluginId, long bytes) =>
        _disk.AddOrUpdate(pluginId, 0, (_, used) => Math.Max(0, used - bytes));

    /// <summary>
    /// Null when the send may happen. Counted per second rather than per
    /// total: an uplink allowance is a rate, and a plugin that sent a lot
    /// yesterday has not used anything up today.
    /// </summary>
    public PluginRefusal? AccountUpload(Ulid pluginId, long bytes)
    {
        PluginQuota quota = quotas.For(pluginId);

        // Whole seconds, because a rate is a rate per second and a window that
        // slides would make two plugins with identical traffic answer
        // differently depending on when they started.
        DateTimeOffset second = new(
            clock.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
            TimeSpan.Zero
        );

        (DateTimeOffset Second, long Bytes) spent = _upload.AddOrUpdate(
            pluginId,
            (second, bytes),
            (_, held) => held.Second == second ? (second, held.Bytes + bytes) : (second, bytes)
        );

        if (spent.Bytes <= quota.UploadBytesPerSecond)
            return null;

        return Counted(
            pluginId,
            new(
                PluginRefusalCodes.QuotaUploadExceeded,
                pluginId.ToString(),
                $"The plugin is sending faster than the {quota.UploadBytesPerSecond / 1024} KB a second this server allows it.",
                "An uplink is shared with everything else on this connection, including the people watching.",
                "Raise the plugin's upload allowance on its page if this server's connection can carry it.",
                PluginRefusalSeverity.Degraded
            )
        );
    }

    /// <summary>
    /// Always null. Fetching spends the owner's own line on something they
    /// installed on purpose, so it is not metered, and a member saying so is
    /// harder to misread than an absence.
    /// </summary>
    public PluginRefusal? AccountDownload(Ulid pluginId, long bytes) => null;

    /// <summary>
    /// The same bytes, counted on their way out and slowed when the plugin is
    /// sending faster than it may.
    /// </summary>
    public Stream Metered(Ulid pluginId, Stream inner) =>
        new PluginUploadMeteredStream(inner, pluginId, this, clock);

    private PluginRefusal Counted(Ulid pluginId, PluginRefusal refusal)
    {
        counter?.Count(pluginId, refusal);

        return refusal;
    }
}
