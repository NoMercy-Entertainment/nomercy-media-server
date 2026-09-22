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
using NoMercy.PluginSdk.Watchdog;

namespace NoMercy.PluginSdk.Quotas;

/// <summary>
/// The allowances the owner set, per plugin, kept on disk.
/// <para>
/// A plugin with nothing saved gets this machine's share, so a new install
/// works without the owner deciding anything first.
/// </para>
/// </summary>
public class PluginQuotaStore(
    PluginQuota? machineDefault = null,
    long? uplinkBytesPerSecond = null,
    string? folder = null
) : IPluginQuotaSource, IPluginResourceCeilingSource
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private readonly PluginQuota _default = machineDefault ?? Machine(uplinkBytesPerSecond);

    /// <summary>
    /// This machine's share. The upload allowance is unlimited until the
    /// uplink has actually been measured: a cap worked out from a number this
    /// server made up would slow a plugin for no reason anyone could point at.
    /// </summary>
    public static PluginQuota Machine(long? uplinkBytesPerSecond)
    {
        PluginQuota shares = PluginQuota.DefaultFor(
            Environment.ProcessorCount,
            // What the process can see, which is the container's limit when
            // there is one. A share of the host's memory would be a share of
            // memory this server is not allowed to touch.
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            uplinkBytesPerSecond ?? 1
        );

        if (uplinkBytesPerSecond is null)
            return shares with { UploadBytesPerSecond = long.MaxValue };

        return shares;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string File => Path.Combine(_folder, "quotas.json");

    private Dictionary<string, PluginQuota>? _cached;

    public PluginQuota For(Ulid pluginId) =>
        Current().TryGetValue(pluginId.ToString(), out PluginQuota? quota) ? quota : _default;

    PluginResourceCeilings IPluginResourceCeilingSource.For(Ulid pluginId)
    {
        PluginQuota quota = For(pluginId);

        return new(quota.CpuPercent, quota.MemoryBytes);
    }

    public void Save(Ulid pluginId, PluginQuota quota)
    {
        Dictionary<string, PluginQuota> current = new(Current()) { [pluginId.ToString()] = quota };

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(current, Json));
        _cached = current;
    }

    private Dictionary<string, PluginQuota> Current()
    {
        if (_cached is not null)
            return _cached;

        if (!System.IO.File.Exists(File))
            return _cached = [];

        try
        {
            _cached =
                JsonSerializer.Deserialize<Dictionary<string, PluginQuota>>(
                    System.IO.File.ReadAllText(File),
                    Json
                ) ?? [];
        }
        catch (JsonException)
        {
            // A file nobody can read means nobody set an allowance, which is
            // this machine's share. Reading it as no allowance at all would
            // stop every plugin over a corrupt file.
            _cached = [];
        }

        return _cached;
    }
}
