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
using NoMercy.PluginSdk.Runtime;

namespace NoMercy.PluginSdk.Browser;

internal sealed class PluginBrowserRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Ulid, PluginBrowser> _browsers = new();

    internal IPluginBrowser For(
        Ulid pluginId,
        IPluginCapabilityBroker broker,
        PluginCapabilities? capabilities,
        Func<IReadOnlyList<string>> grantedHosts,
        IPluginResourceLedger ledger
    ) =>
        _browsers.AddOrUpdate(
            pluginId,
            _ => new PluginBrowser(pluginId, broker, capabilities, grantedHosts, ledger),
            (_, current) =>
                current.IsDisposed
                    ? new PluginBrowser(pluginId, broker, capabilities, grantedHosts, ledger)
                    : current
        );

    internal async Task ReleaseAsync(Ulid pluginId)
    {
        if (_browsers.TryRemove(pluginId, out PluginBrowser? browser))
            await browser.DisposeAsync();
    }

    public void Dispose()
    {
        foreach (PluginBrowser browser in _browsers.Values)
        {
            try
            {
                browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // One failed browser cleanup must not strand the others.
            }
        }
    }
}
