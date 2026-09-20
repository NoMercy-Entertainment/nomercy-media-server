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

using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;

namespace NoMercy.Plugins.Capabilities;

public interface IPluginConsentStore
{
    bool Contains(Ulid pluginId);
    PluginConsentGrant? Get(Ulid pluginId);
    void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion);
    void Remove(Ulid pluginId);

    /// <summary>
    /// Writes a record back whole. Per-capability answers are a read, a change
    /// and a write, and <see cref="Add"/> can only replace the capability set.
    /// </summary>
    void Save(Ulid pluginId, PluginConsentGrant grant);
}

public class PluginConsentService(
    IPluginConsentStore store,
    ILogger? logger = null,
    IEventBus? events = null
) : IPluginConsentService
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

        // Upgraded in place, and the old id-only entry goes with it, so the
        // next read has a real record to compare a later manifest against.
        // Without this step a genuine widening would keep migrating instead
        // of asking.
        if (grant.PredatesCapabilityTracking)
        {
            // A record from before consent carried a capability list says only
            // that the owner said yes, never to what. The installed manifest is
            // the closest thing to what they saw, so it is what gets written,
            // and the log names it because the owner cannot read it back off a
            // record that never held it.
            store.Add(pluginId, capabilities, installedVersion);
            logger?.LogInformation(
                "Plugin {PluginId}: an approval from before capabilities were recorded now covers {Capabilities}, read from the installed manifest at {Version}.",
                pluginId,
                Describe(capabilities),
                installedVersion
            );
            return true;
        }

        return !PluginCapabilityGuard.HasWidened(grant.Capabilities, capabilities);
    }

    public PluginCapabilities? ConsentedCapabilities(Ulid pluginId) =>
        store.Get(pluginId)?.Capabilities;

    public void GrantConsent(
        Ulid pluginId,
        PluginCapabilities? capabilities,
        Version manifestVersion
    ) => store.Add(pluginId, capabilities, manifestVersion);

    public void RevokeConsent(Ulid pluginId) => store.Remove(pluginId);

    private static string Describe(PluginCapabilities? capabilities)
    {
        if (capabilities is null)
            return "nothing beyond the ordinary set";

        List<string> parts = [];

        if (capabilities.Hooks.Count > 0)
            parts.Add($"hooks {string.Join(", ", capabilities.Hooks)}");

        if (capabilities.Rest)
            parts.Add(capabilities.RestAnonymous ? "its own endpoints, open" : "its own endpoints");

        if (capabilities.Ws)
            parts.Add("a socket");

        if (capabilities.Network?.Hosts.Count > 0)
            parts.Add($"network hosts {string.Join(", ", capabilities.Network.Hosts)}");

        return parts.Count > 0 ? string.Join("; ", parts) : "nothing beyond the ordinary set";
    }

    public void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion)
    {
        PluginConsentGrant grant = store.Get(pluginId) ?? new();
        grant.ApprovedCapabilities[capability] = manifestVersion.ToString();
        store.Save(pluginId, grant);
        PluginAccessAnnouncement.Changed(events, pluginId);
    }

    public void RevokeCapability(Ulid pluginId, string capability)
    {
        PluginConsentGrant? grant = store.Get(pluginId);

        if (grant is null || !grant.ApprovedCapabilities.Remove(capability))
            return;

        store.Save(pluginId, grant);
        PluginAccessAnnouncement.Changed(events, pluginId);
    }

    public bool IsApproved(Ulid pluginId, string capability) =>
        store.Get(pluginId)?.ApprovedCapabilities.ContainsKey(capability) == true;

    public Version? ApprovedAt(Ulid pluginId, string capability)
    {
        string? version =
            store.Get(pluginId) is { } grant
            && grant.ApprovedCapabilities.TryGetValue(capability, out string? at)
                ? at
                : null;

        return version is null ? null : Version.Parse(version);
    }
}
