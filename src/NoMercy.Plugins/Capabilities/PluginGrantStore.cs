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
/// What the owner has allowed each plugin, and what each plugin has asked for.
/// </summary>
public interface IPluginGrantStore
{
    IReadOnlyList<string> Granted(Ulid pluginId, string kind);
    bool Holds(Ulid pluginId, string kind, string value);
    void Grant(Ulid pluginId, string kind, string value);
    void Revoke(Ulid pluginId, string kind, string value);

    /// <summary>
    /// Takes away everything one plugin holds, whatever the kind, and clears
    /// what it was still asking for.
    /// <para>
    /// Withdrawing consent used to walk a list of kinds written out by hand, so
    /// a kind that list did not name survived the owner saying no —
    /// <see cref="PluginGrantKind.PlayerSource"/> did, and every
    /// <see cref="PluginGrantKind.ForCapability"/> kind is built at runtime and
    /// could never have been on it. Asking the store to empty itself for one
    /// plugin removes the list, so there is nothing left to forget.
    /// </para>
    /// </summary>
    void RevokeAll(Ulid pluginId);

    /// <summary>Records a plugin's request. Asking twice for one thing records once.</summary>
    void Request(Ulid pluginId, string kind, string value, string reason);

    /// <summary>Everything waiting on the owner, for the dashboard to present.</summary>
    IReadOnlyList<PluginGrantRequest> PendingRequests();

    /// <summary>Clears a request once the owner has answered it either way.</summary>
    void ClearRequest(Ulid pluginId, string kind, string value);
}

public class PluginGrantRecord
{
    public List<PluginGrantEntry> Grants { get; init; } = [];
    public List<PluginGrantRequestEntry> Requests { get; init; } = [];
}

public class PluginGrantEntry
{
    public Ulid PluginId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public class PluginGrantRequestEntry
{
    public Ulid PluginId { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime RequestedAt { get; init; }
}

/// <summary>
/// Grants held in the platform-scoped configuration file, beside the consent
/// set they generalise.
/// <para>
/// Deliberately not per-plugin storage: a plugin must not be able to edit the
/// record of what it was allowed to do.
/// </para>
/// </summary>
public class ConfigPluginGrantStore(IPluginConfiguration configuration) : IPluginGrantStore
{
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Granted(Ulid pluginId, string kind)
    {
        PluginGrantRecord record = Read();
        return
        [
            .. record
                .Grants.Where(entry =>
                    entry.PluginId == pluginId
                    && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                )
                .Select(entry => entry.Value),
        ];
    }

    public bool Holds(Ulid pluginId, string kind, string value)
    {
        IReadOnlyList<string> granted = Granted(pluginId, kind);

        return granted.Any(entry =>
            entry == PluginGrant.Everything
            || string.Equals(entry, value, StringComparison.OrdinalIgnoreCase)
        );
    }

    public void Grant(Ulid pluginId, string kind, string value)
    {
        lock (_gate)
        {
            PluginGrantRecord record = Read();

            if (
                record.Grants.Any(entry =>
                    entry.PluginId == pluginId
                    && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
                )
            )
                return;

            record.Grants.Add(
                new()
                {
                    PluginId = pluginId,
                    Kind = kind,
                    Value = value,
                }
            );

            record.Requests.RemoveAll(entry =>
                entry.PluginId == pluginId
                && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
            );

            configuration.SaveConfiguration(record);
        }
    }

    public void Revoke(Ulid pluginId, string kind, string value)
    {
        lock (_gate)
        {
            PluginGrantRecord record = Read();

            int removed = record.Grants.RemoveAll(entry =>
                entry.PluginId == pluginId
                && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
            );

            if (removed > 0)
                configuration.SaveConfiguration(record);
        }
    }

    public void RevokeAll(Ulid pluginId)
    {
        lock (_gate)
        {
            PluginGrantRecord record = Read();

            int removed = record.Grants.RemoveAll(entry => entry.PluginId == pluginId);
            removed += record.Requests.RemoveAll(entry => entry.PluginId == pluginId);

            if (removed > 0)
                configuration.SaveConfiguration(record);
        }
    }

    public void Request(Ulid pluginId, string kind, string value, string reason)
    {
        lock (_gate)
        {
            PluginGrantRecord record = Read();

            // Already allowed, or already asked. Neither needs a second prompt,
            // and a plugin that asks in a loop must not be able to fill the
            // owner's dashboard with the same request.
            if (Holds(pluginId, kind, value))
                return;

            if (
                record.Requests.Any(entry =>
                    entry.PluginId == pluginId
                    && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
                )
            )
                return;

            record.Requests.Add(
                new()
                {
                    PluginId = pluginId,
                    Kind = kind,
                    Value = value,
                    Reason = reason,
                    RequestedAt = DateTime.UtcNow,
                }
            );

            configuration.SaveConfiguration(record);
        }
    }

    public IReadOnlyList<PluginGrantRequest> PendingRequests() =>
        [
            .. Read()
                .Requests.Select(entry => new PluginGrantRequest(
                    entry.PluginId,
                    entry.Kind,
                    entry.Value,
                    entry.Reason,
                    entry.RequestedAt
                )),
        ];

    public void ClearRequest(Ulid pluginId, string kind, string value)
    {
        lock (_gate)
        {
            PluginGrantRecord record = Read();

            int removed = record.Requests.RemoveAll(entry =>
                entry.PluginId == pluginId
                && string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
            );

            if (removed > 0)
                configuration.SaveConfiguration(record);
        }
    }

    private PluginGrantRecord Read() =>
        configuration.GetConfiguration<PluginGrantRecord>() ?? new();
}
