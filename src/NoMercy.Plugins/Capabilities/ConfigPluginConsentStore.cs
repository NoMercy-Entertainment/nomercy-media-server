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

    /// <summary>
    /// An id-only record from before consent wrote down what it was granted
    /// for. It carries no capability set, so it can never be compared against a
    /// manifest; it has to be upgraded from the installed manifest first.
    /// </summary>
    public bool PredatesCapabilityTracking { get; init; }

    /// <summary>
    /// Which capabilities the owner said yes to, and the plugin version that
    /// asked.
    /// <para>
    /// Per capability rather than one yes for the plugin, because an owner who
    /// wants a radio plugin to reach the internet and not to spawn processes
    /// had no way to say so: the only answers were everything or nothing, and
    /// nothing meant the plugin did not run. The version is recorded per entry
    /// so a plugin that later asks for more leaves the new one pending without
    /// disturbing what was already approved.
    /// </para>
    /// </summary>
    public Dictionary<string, string> ApprovedCapabilities { get; init; } =
        new(StringComparer.Ordinal);
}

public class PluginConsentRecord
{
    // The shape written before consent recorded what it was granted for. Read
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

        return record.GrantedPluginIds.Contains(pluginId)
            || record.ConsentGrants.ContainsKey(Key(pluginId));
    }

    public PluginConsentGrant? Get(Ulid pluginId)
    {
        PluginConsentRecord? record = configuration.GetConfiguration<PluginConsentRecord>();
        if (record is null)
            return null;

        if (record.ConsentGrants.TryGetValue(Key(pluginId), out PluginConsentGrant? grant))
            return grant;

        // An entry consented before capabilities were recorded. It is flagged
        // rather than returned empty, because an empty capability set compares
        // as "the owner approved nothing" and would disable every plugin they
        // had already said yes to.
        return record.GrantedPluginIds.Contains(pluginId)
            ? new PluginConsentGrant { PredatesCapabilityTracking = true }
            : null;
    }

    public void Add(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion)
    {
        PluginConsentRecord record = configuration.GetConfiguration<PluginConsentRecord>() ?? new();

        record.GrantedPluginIds.Remove(pluginId);
        // Approvals already given are carried over. Replacing the record
        // wholesale here turned an update into a silent re-approval of
        // everything the owner had said no to.
        record.ConsentGrants.TryGetValue(Key(pluginId), out PluginConsentGrant? existing);

        record.ConsentGrants[Key(pluginId)] = new PluginConsentGrant
        {
            Capabilities = capabilities,
            ManifestVersion = manifestVersion.ToString(),
            ApprovedCapabilities = existing?.ApprovedCapabilities ?? new(StringComparer.Ordinal),
        };

        configuration.SaveConfiguration(record);
    }

    public void Save(Ulid pluginId, PluginConsentGrant grant)
    {
        PluginConsentRecord record = configuration.GetConfiguration<PluginConsentRecord>() ?? new();

        record.GrantedPluginIds.Remove(pluginId);
        record.ConsentGrants[Key(pluginId)] = grant;

        configuration.SaveConfiguration(record);
    }

    public void Remove(Ulid pluginId)
    {
        PluginConsentRecord? record = configuration.GetConfiguration<PluginConsentRecord>();
        if (record is null)
            return;

        bool removedOldEntry = record.GrantedPluginIds.Remove(pluginId);
        bool removedGrant = record.ConsentGrants.Remove(Key(pluginId));

        if (removedOldEntry || removedGrant)
            configuration.SaveConfiguration(record);
    }

    private static string Key(Ulid pluginId) => pluginId.ToString();
}
