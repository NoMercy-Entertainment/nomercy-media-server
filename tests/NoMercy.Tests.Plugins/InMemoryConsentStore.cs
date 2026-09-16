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

    public void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion) =>
        _granted[pluginId] = new PluginConsentGrant
        {
            Capabilities = capabilities,
            ManifestVersion = manifestVersion.ToString(),
        };

    public void Remove(Ulid pluginId) => _granted.Remove(pluginId);
}
