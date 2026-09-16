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

/// <summary>
/// What the owner was told about when they consented: the capability set and
/// the manifest version it came from, so a later widened manifest can be told
/// apart from the one the owner actually approved.
/// </summary>
public class PluginConsentGrant
{
    public PluginCapabilities? Capabilities { get; init; }
    public string? ManifestVersion { get; init; }
}

public class PluginConsentRecord
{
    // Legacy shape from before consent recorded what it was granted for. Read
    // for backward compatibility only; new grants are written to Grants.
    public List<Ulid> GrantedPluginIds { get; init; } = [];

    // Named distinctly from PluginGrantRecord.Grants: both records share one
    // platform config file, and Merge() replaces a top-level JSON key
    // wholesale for whichever record wrote it last - the two "Grants" would
    // have overwritten each other every time both stores saved.
    //
    // Keyed by the string form of the plugin id: System.Text.Json cannot use
    // Ulid as a dictionary key through PluginIdJsonConverter, which only
    // implements value (de)serialization, not property-name (de)serialization.
    public Dictionary<string, PluginConsentGrant> ConsentGrants { get; init; } = [];
}

// Backed by IPluginConfiguration under a platform-scoped data folder (not a
// per-plugin one) so the granted set survives process restarts and is shared
// across every plugin's consent check.
public class ConfigPluginConsentStore(IPluginConfiguration configuration) : IPluginConsentStore
{
    public bool Contains(Ulid pluginId)
    {
        PluginConsentRecord? record = configuration.GetConfiguration<PluginConsentRecord>();
        if (record is null)
            return false;

        return record.GrantedPluginIds.Contains(pluginId) || record.ConsentGrants.ContainsKey(Key(pluginId));
    }

    public PluginConsentGrant? Get(Ulid pluginId)
    {
        PluginConsentRecord? record = configuration.GetConfiguration<PluginConsentRecord>();
        if (record is null)
            return null;

        if (record.ConsentGrants.TryGetValue(Key(pluginId), out PluginConsentGrant? grant))
            return grant;

        // A legacy entry was consented before capabilities were recorded, so
        // there is nothing to compare a widened manifest against.
        return record.GrantedPluginIds.Contains(pluginId) ? new PluginConsentGrant() : null;
    }

    public void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion)
    {
        PluginConsentRecord record = configuration.GetConfiguration<PluginConsentRecord>() ?? new();

        record.GrantedPluginIds.Remove(pluginId);
        record.ConsentGrants[Key(pluginId)] = new PluginConsentGrant
        {
            Capabilities = capabilities,
            ManifestVersion = manifestVersion.ToString(),
        };

        configuration.SaveConfiguration(record);
    }

    public void Remove(Ulid pluginId)
    {
        PluginConsentRecord? record = configuration.GetConfiguration<PluginConsentRecord>();
        if (record is null)
            return;

        bool removedLegacy = record.GrantedPluginIds.Remove(pluginId);
        bool removedGrant = record.ConsentGrants.Remove(Key(pluginId));

        if (removedLegacy || removedGrant)
            configuration.SaveConfiguration(record);
    }

    private static string Key(Ulid pluginId) => pluginId.ToString();
}
