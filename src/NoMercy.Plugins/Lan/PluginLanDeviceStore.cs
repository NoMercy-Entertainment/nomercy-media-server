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

/// <summary>
/// The devices the owner added, kept on disk.
/// <para>
/// On disk because the television in the living room does not come back and
/// ask for a new address after a restart. A credential held in memory would
/// stop working every time the server did.
/// </para>
/// <para>
/// Every read and write goes through one lock. A revocation and an addition
/// each read the rows, change them and write the file; without the lock the
/// one that wrote last would bring back what the other had changed, such as a
/// device the owner had just revoked.
/// </para>
/// </summary>
public class PluginLanDeviceStore(string? folder = null) : IPluginLanDeviceStore
{
    private readonly string _folder = folder ?? AppFiles.PluginConfigPath;

    private readonly Lock _gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private string File => Path.Combine(_folder, "lan-devices.json");

    private List<PluginLanDevice>? _cached;

    public void Add(PluginLanDevice device)
    {
        lock (_gate)
            Save([.. Current(), device]);
    }

    public PluginLanDevice? Find(Ulid pluginId, string deviceId)
    {
        lock (_gate)
            return Current()
                .FirstOrDefault(device =>
                    device.PluginId == pluginId && device.DeviceId == deviceId
                );
    }

    public IReadOnlyList<PluginLanDevice> For(Ulid pluginId)
    {
        lock (_gate)
            return [.. Current().Where(device => device.PluginId == pluginId)];
    }

    public void Revoke(Ulid pluginId, string deviceId)
    {
        lock (_gate)
            Save([
                .. Current()
                    .Select(device =>
                        device.PluginId == pluginId && device.DeviceId == deviceId
                            ? device with
                            {
                                Revoked = true,
                            }
                            : device
                    ),
            ]);
    }

    /// <summary>Called under <see cref="_gate" />.</summary>
    private void Save(List<PluginLanDevice> devices)
    {
        Directory.CreateDirectory(_folder);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(devices, Json));
        _cached = devices;
    }

    /// <summary>Called under <see cref="_gate" />.</summary>
    private List<PluginLanDevice> Current()
    {
        if (_cached is not null)
            return _cached;

        if (!System.IO.File.Exists(File))
            return _cached = [];

        List<StoredDevice> stored;

        try
        {
            stored =
                JsonSerializer.Deserialize<List<StoredDevice>>(
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
            return _cached = [];
        }

        List<PluginLanDevice> devices = [];
        bool hashedNow = false;

        foreach (StoredDevice row in stored)
        {
            string? hash = row.CredentialHash;

            // A row written before credentials were hashed. Hashed here, once,
            // and the file rewritten, so the device keeps working and the
            // credential no longer sits on disk.
            if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(row.Credential))
            {
                hash = PluginLanDeviceMinter.HashOf(row.Credential);
                hashedNow = true;
            }

            // A row with neither can match no credential; it is dropped
            // rather than kept as one that might.
            if (string.IsNullOrEmpty(hash))
            {
                hashedNow = true;
                continue;
            }

            devices.Add(
                new(row.PluginId, row.DeviceId, row.Name, hash, row.CreatedAt, row.Revoked)
            );
        }

        if (hashedNow)
            Save(devices);

        return _cached = devices;
    }

    /// <summary>
    /// A row as it may be on disk: hashed, or from before hashing with the
    /// credential in the clear.
    /// </summary>
    private sealed record StoredDevice(
        Ulid PluginId,
        string DeviceId,
        string Name,
        string? Credential,
        string? CredentialHash,
        DateTimeOffset CreatedAt,
        bool Revoked
    );
}
