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

namespace NoMercy.Plugins.Capabilities;

public interface IPluginConsentStore
{
    bool Contains(Ulid pluginId);
    PluginConsentGrant? Get(Ulid pluginId);
    void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion);
    void Remove(Ulid pluginId);
}

public class PluginConsentService(IPluginConsentStore store) : IPluginConsentService
{
    public bool IsBaseline(PluginCapabilities? capabilities)
    {
        if (capabilities is null)
            return true;

        if (capabilities.Rest || capabilities.Ws || capabilities.Network is not null)
            return false;

        return capabilities.Hooks.All(PluginHookCapability.Baseline.Contains);
    }

    public bool HasConsent(Ulid pluginId) => store.Contains(pluginId);

    public bool ConsentCoversCapabilities(
        Ulid pluginId,
        PluginCapabilities? capabilities,
        Version installedVersion
    )
    {
        PluginConsentGrant? grant = store.Get(pluginId);
        if (grant is null)
            return false;

        // Upgraded in place, and the legacy id goes with it, so the next read
        // has a real record to compare a later manifest against. Without this
        // step a genuine widening would keep migrating instead of asking.
        if (grant.IsLegacy)
        {
            store.Add(pluginId, capabilities, installedVersion);
            return true;
        }

        return !PluginCapabilityGuard.HasWidened(grant.Capabilities, capabilities);
    }

    public void GrantConsent(
        Ulid pluginId,
        PluginCapabilities? capabilities,
        Version manifestVersion
    ) => store.Add(pluginId, capabilities, manifestVersion);

    public void RevokeConsent(Ulid pluginId) => store.Remove(pluginId);
}
