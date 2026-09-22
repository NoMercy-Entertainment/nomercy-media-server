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

namespace NoMercy.PluginSdk.Capabilities;

public static class PluginCapabilityGuard
{
    public static bool DeclaresHook(PluginCapabilities? capabilities, string hook)
    {
        if (capabilities is null)
            return PluginHookCapability.Baseline.Contains(hook);

        return capabilities.Hooks.Contains(hook, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="current"/> asks for anything the owner was never
    /// asked about when they consented to <paramref name="consented"/>.
    /// <para>
    /// Narrowing what a plugin asks for never needs a new prompt; adding a
    /// hook, turning on rest/ws, opening a route to anonymous callers, asking
    /// for a place in the main navigation, or naming a new network host does,
    /// because the owner's earlier "yes" was scoped to a smaller request.
    /// </para>
    /// <para>
    /// Moving or renaming a mount inside a section the owner already approved
    /// is not a widening. Where a plugin appears in its own section is its own
    /// business; asking to sit beside the app's own sections is not.
    /// </para>
    /// </summary>
    public static bool HasWidened(PluginCapabilities? consented, PluginCapabilities? current)
    {
        if (current is null)
            return false;

        List<string> consentedHooksBaseline =
            consented?.Hooks ?? [.. PluginHookCapability.Baseline];

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

        // Opening an endpoint to callers with no token at all is the widest
        // ask a manifest carries, and it was not compared: a plugin could go
        // from "every route needs a token" to "this one does not" on a consent
        // the owner gave to the first of those.
        if (current.RestAnonymous && consented?.RestAnonymous != true)
            return true;

        // A place in the main navigation is a request the owner answers, so a
        // plugin cannot grow into one on a consent given when it had none.
        List<string> consentedTopLevel =
        [
            .. (consented?.Ui?.Mounts ?? [])
                .Where(mount => mount.RequestsTopLevel)
                .Select(mount => mount.Route),
        ];

        if (
            (current.Ui?.Mounts ?? [])
                .Where(mount => mount.RequestsTopLevel)
                .Any(mount =>
                    !consentedTopLevel.Contains(mount.Route, StringComparer.OrdinalIgnoreCase)
                )
        )
            return true;

        List<string> consentedHosts = consented?.Network?.Hosts ?? [];
        List<string> currentHosts = current.Network?.Hosts ?? [];
        return currentHosts.Any(host =>
            !consentedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
        );
    }
}
