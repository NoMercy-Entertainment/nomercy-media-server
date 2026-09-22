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

namespace NoMercy.PluginSdk.Lan;

/// <summary>Where the device rows live.</summary>
public interface IPluginLanDeviceStore
{
    void Add(PluginLanDevice device);

    PluginLanDevice? Find(Ulid pluginId, string deviceId);

    IReadOnlyList<PluginLanDevice> For(Ulid pluginId);

    void Revoke(Ulid pluginId, string deviceId);
}

/// <summary>
/// The devices the owner added, kept on disk.
/// <para>
/// On disk because the television in the living room does not come back and
/// ask for a new address after a restart. A credential held in memory would
/// stop working every time the server did.
/// </para>
/// </summary>
public class PluginLanDeviceStore(string? folder = null) : IPluginLanDeviceStore
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string File => Path.Combine(_folder, "lan-devices.json");

    private List<PluginLanDevice>? _cached;

    public void Add(PluginLanDevice device)
    {
        List<PluginLanDevice> current = [.. Current(), device];

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(current, Json));
        _cached = current;
    }

    public PluginLanDevice? Find(Ulid pluginId, string deviceId) =>
        Current()
            .FirstOrDefault(device => device.PluginId == pluginId && device.DeviceId == deviceId);

    public IReadOnlyList<PluginLanDevice> For(Ulid pluginId) =>
        [.. Current().Where(device => device.PluginId == pluginId)];

    public void Revoke(Ulid pluginId, string deviceId)
    {
        List<PluginLanDevice> current =
        [
            .. Current()
                .Select(device =>
                    device.PluginId == pluginId && device.DeviceId == deviceId
                        ? device with
                        {
                            Revoked = true,
                        }
                        : device
                ),
        ];

        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(current, Json));
        _cached = current;
    }

    private List<PluginLanDevice> Current()
    {
        if (_cached is not null)
            return _cached;

        if (!System.IO.File.Exists(File))
            return _cached = [];

        try
        {
            _cached =
                JsonSerializer.Deserialize<List<PluginLanDevice>>(
                    System.IO.File.ReadAllText(File),
                    Json
                ) ?? [];
        }
        catch (JsonException)
        {
            // A file nobody can read means no device is known, so every device
            // is refused until the owner adds it again. Reading it as "every
            // credential works" over a corrupt file is the other direction, and
            // that one opens the server.
            _cached = [];
        }

        return _cached;
    }
}
