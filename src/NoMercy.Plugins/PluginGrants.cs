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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk;

/// <summary>
/// One plugin's view of the grant store.
/// <para>
/// The plugin id is bound here rather than passed by the caller, so a plugin
/// cannot read or request against another plugin's id by supplying one.
/// </para>
/// </summary>
public class PluginGrants(Ulid pluginId, IPluginGrantStore store) : IPluginGrants
{
    private const int MaxReasonLength = 500;

    public Task<bool> HasAsync(string kind, string value, CancellationToken ct = default) =>
        Task.FromResult(store.Holds(pluginId, kind, value));

    public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default) =>
        Task.FromResult(store.Granted(pluginId, kind));

    public Task RequestAsync(
        string kind,
        string value,
        string reason,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(value))
            return Task.CompletedTask;

        // The reason is shown to the owner, so it is plugin-authored text on a
        // trusted surface. Bounded here; escaped where it is rendered.
        string trimmed = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason;

        store.Request(pluginId, kind, value, trimmed);
        return Task.CompletedTask;
    }
}
