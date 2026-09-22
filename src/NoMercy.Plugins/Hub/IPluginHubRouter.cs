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

namespace NoMercy.PluginSdk.Hub;

/// <summary>
/// Which plugin a hub message belongs to, and whether it is allowed to arrive.
/// <para>
/// One hub multiplexes every plugin rather than each plugin mapping its own
/// endpoint: a client opens one connection, and a plugin that is disabled
/// mid-session stops receiving without anything being unmapped.
/// </para>
/// </summary>
public interface IPluginHubRouter
{
    void Register(IPluginHubHandler handler);

    void Unregister(Ulid pluginId);

    /// <summary>
    /// The delegate-backed handler for this plugin, created on first ask.
    /// <para>
    /// Kept apart from <see cref="Register" />'s handler rather than replacing
    /// it, so a plugin that implements <see cref="IPluginHubHandler" /> and also
    /// registers delegates keeps both. Keying one dictionary by plugin id would
    /// have silently dropped whichever arrived first.
    /// </para>
    /// </summary>
    PluginDelegateHubHandler DelegateHandlerFor(Ulid pluginId);

    /// <summary>
    /// Hands the message to the plugin's handler, or drops it. Dropped when no
    /// handler is registered, when the plugin is not active, or when it never
    /// declared the <c>ws</c> capability — a plugin does not get a live channel
    /// it did not ask the owner for.
    /// </summary>
    Task<bool> RouteAsync(
        Ulid pluginId,
        PluginHubMessage message,
        IPluginHubClient client,
        CancellationToken ct
    );
}
