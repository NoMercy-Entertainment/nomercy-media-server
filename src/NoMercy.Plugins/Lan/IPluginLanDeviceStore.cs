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

namespace NoMercy.PluginSdk.Lan;

/// <summary>Where the device rows live.</summary>
public interface IPluginLanDeviceStore
{
    void Add(PluginLanDevice device);

    PluginLanDevice? Find(Ulid pluginId, string deviceId);

    IReadOnlyList<PluginLanDevice> For(Ulid pluginId);

    void Revoke(Ulid pluginId, string deviceId);
}
