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

public static class PluginCapabilityGuard
{
    private static readonly HashSet<string> ImplicitBaseline = new(StringComparer.OrdinalIgnoreCase)
    {
        PluginHookCapability.MediaSource,
        PluginHookCapability.Metadata,
        PluginHookCapability.Ui,
    };

    public static bool DeclaresHook(PluginCapabilities? capabilities, string hook)
    {
        if (capabilities is null)
            return ImplicitBaseline.Contains(hook);

        return capabilities.Hooks.Contains(hook, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="current"/> asks for anything the owner was never
    /// asked about when they consented to <paramref name="consented"/>.
    /// <para>
    /// Narrowing what a plugin asks for never needs a new prompt; adding a
    /// hook, turning on rest/ws, or naming a new network host does, because
    /// the owner's earlier "yes" was scoped to a smaller request.
    /// </para>
    /// </summary>
    public static bool HasWidened(PluginCapabilities? consented, PluginCapabilities? current)
    {
        if (current is null)
            return false;

        List<string> consentedHooksBaseline = consented?.Hooks ?? [.. ImplicitBaseline];

        if (
            current.Hooks.Any(hook =>
                !consentedHooksBaseline.Contains(hook, StringComparer.OrdinalIgnoreCase)
            )
        )
            return true;

        if (current.Rest && consented?.Rest != true)
            return true;

        if (current.Ws && consented?.Ws != true)
            return true;

        List<string> consentedHosts = consented?.Network?.Hosts ?? [];
        List<string> currentHosts = current.Network?.Hosts ?? [];
        return currentHosts.Any(host => !consentedHosts.Contains(host, StringComparer.OrdinalIgnoreCase));
    }
}
