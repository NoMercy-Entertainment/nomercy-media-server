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

namespace NoMercy.Tests.Plugins;

internal sealed class InMemoryConsentStore : IPluginConsentStore
{
    private readonly Dictionary<Ulid, PluginConsentGrant> _granted = [];

    public bool Contains(Ulid pluginId) => _granted.ContainsKey(pluginId);

    public PluginConsentGrant? Get(Ulid pluginId) =>
        _granted.TryGetValue(pluginId, out PluginConsentGrant? grant) ? grant : null;

    public void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion)
    {
        // Carries the per-capability answers over, the way the real store does.
        // Dropping them here would let a test pass on a store that silently
        // re-approves everything the owner said no to.
        _granted.TryGetValue(pluginId, out PluginConsentGrant? existing);

        _granted[pluginId] = new PluginConsentGrant
        {
            Capabilities = capabilities,
            ManifestVersion = manifestVersion.ToString(),
            ApprovedCapabilities = existing?.ApprovedCapabilities ?? new(StringComparer.Ordinal),
        };
    }

    public void Save(Ulid pluginId, PluginConsentGrant grant) => _granted[pluginId] = grant;

    public void Remove(Ulid pluginId) => _granted.Remove(pluginId);
}
