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

using NoMercy.PluginSdk.Lan;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>Device rows kept in memory, so a test never writes the owner's file.</summary>
public sealed class InMemoryLanDeviceStore : IPluginLanDeviceStore
{
    private readonly Lock _lock = new();
    private readonly List<PluginLanDevice> _devices = [];

    public void Add(PluginLanDevice device)
    {
        lock (_lock)
            _devices.Add(device);
    }

    public PluginLanDevice? Find(Ulid pluginId, string deviceId)
    {
        lock (_lock)
            return _devices.FirstOrDefault(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );
    }

    public IReadOnlyList<PluginLanDevice> For(Ulid pluginId)
    {
        lock (_lock)
            return [.. _devices.Where(device => device.PluginId == pluginId)];
    }

    public void Revoke(Ulid pluginId, string deviceId)
    {
        lock (_lock)
        {
            int index = _devices.FindIndex(device =>
                device.PluginId == pluginId && device.DeviceId == deviceId
            );

            if (index >= 0)
                _devices[index] = _devices[index] with { Revoked = true };
        }
    }
}
