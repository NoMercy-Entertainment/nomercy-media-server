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
using NoMercy.Plugins.Capabilities;
using NoMercy.Storage;

namespace NoMercy.Plugins;

public class PluginDataPurge(
    string pluginsPath,
    IStorage storage,
    IPluginConsentService consentService,
    IPluginGrantStore grantStore,
    IPluginConfiguration platformConfiguration
) : IPluginDataPurge
{
    public async Task PurgeAsync(Ulid pluginId, CancellationToken ct = default)
    {
        string dataFolder = storage.CombinePath(pluginsPath, "data", pluginId.ToString());

        if (await storage.ExistsAsync(dataFolder, ct))
            await storage.DeleteDirectoryAsync(dataFolder, recursive: true, ct);

        consentService.RevokeConsent(pluginId);
        grantStore.RevokeAll(pluginId);
        PluginSecretStore.Purge(pluginId, platformConfiguration);
    }
}
