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

using Microsoft.AspNetCore.DataProtection;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins;

/// <summary>
/// A plugin's secrets, protected with <see cref="IDataProtector"/> and held in
/// the platform store.
/// <para>
/// The purpose is to make the correct path the easy one. Every plugin author
/// with a password would otherwise rediscover the same procedure — resolve a
/// data-protection provider out of the service provider, reference the
/// abstractions package with runtime assets excluded so the type identity is
/// shared, protect before writing — and most would get some part of it wrong
/// and store plaintext. A rule that depends on every author reimplementing it
/// is a rule that will be broken.
/// </para>
/// <para>
/// The protector's purpose string carries the plugin id, so a value written by
/// one plugin cannot be unprotected by another even if it reaches the stored
/// bytes. Keys are namespaced the same way, so a plugin cannot read across by
/// choosing a clever key.
/// </para>
/// </summary>
public class PluginSecretStore(
    Ulid pluginId,
    IDataProtectionProvider protectionProvider,
    IPluginConfiguration configuration,
    Func<UserId?>? caller = null
) : IPluginSecretStore
{
    private readonly IDataProtector _protector = protectionProvider.CreateProtector(
        $"NoMercy.Plugins.Secrets.{pluginId:D}"
    );

    private readonly Lock _gate = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        GetScopedAsync(Scoped(key));

    private Task<string?> GetScopedAsync(string scopedKey)
    {
        PluginSecretRecord record = Read();

        if (!record.Values.TryGetValue(scopedKey, out string? protectedValue))
            return Task.FromResult<string?>(null);

        try
        {
            return Task.FromResult<string?>(_protector.Unprotect(protectedValue));
        }
        catch (Exception)
        {
            // A value that will not unprotect is a value from a different key
            // ring — a restored backup, a rotated key. Null is the honest
            // answer; throwing here would break a plugin on startup for
            // something it cannot fix.
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return SetScopedAsync(Scoped(key), value);
    }

    private Task SetScopedAsync(string scopedKey, string value)
    {
        lock (_gate)
        {
            PluginSecretRecord record = Read();
            record.Values[scopedKey] = _protector.Protect(value);
            configuration.SaveConfiguration(record);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        DeleteScopedAsync(Scoped(key));

    private Task DeleteScopedAsync(string scopedKey)
    {
        lock (_gate)
        {
            PluginSecretRecord record = Read();

            if (record.Values.Remove(scopedKey))
                configuration.SaveConfiguration(record);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default)
    {
        string prefix = Scoped(string.Empty);

        // Per-user slots are excluded. They live under the same plugin prefix,
        // and a plugin listing its own keys must not be handed one member's
        // key names, let alone every member's.
        IReadOnlyList<string> keys = Read()
            .Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(key => key[prefix.Length..])
            .Where(key => !key.StartsWith("user:", StringComparison.Ordinal))
            .ToList();

        return Task.FromResult(keys);
    }

    /// <summary>
    /// Deletes every secret one plugin stored. Here rather than in the caller
    /// because the key scoping is this class's rule, and a second place that
    /// knows the prefix is a second place that can get it wrong and wipe
    /// another plugin's values.
    /// </summary>
    public static void Purge(Ulid pluginId, IPluginConfiguration configuration)
    {
        PluginSecretRecord record = configuration.GetConfiguration<PluginSecretRecord>() ?? new();
        string prefix = $"{pluginId:D}:";

        List<string> owned =
        [
            .. record.Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)),
        ];

        if (owned.Count == 0)
            return;

        foreach (string key in owned)
            record.Values.Remove(key);

        configuration.SaveConfiguration(record);
    }

    private string Scoped(string key) => $"{pluginId:D}:{key}";

    /// <summary>
    /// The caller's own slot. Separate from the server's rather than a prefix
    /// on the same one, so a provider login one member set cannot be read, or
    /// revoked, by another.
    /// </summary>
    private string ScopedForUser(string key)
    {
        UserId? user =
            caller?.Invoke()
            ?? throw new PluginRefusedException(
                PluginRefusalMessages.SecretHasNoCaller(pluginId.ToString(), key)
            );

        return $"{pluginId:D}:user:{user.Value.Value:D}:{key}";
    }

    public Task<string?> GetForUserAsync(string key, CancellationToken ct = default) =>
        GetScopedAsync(ScopedForUser(key));

    public Task SetForUserAsync(string key, string value, CancellationToken ct = default) =>
        SetScopedAsync(ScopedForUser(key), value);

    public Task DeleteForUserAsync(string key, CancellationToken ct = default) =>
        DeleteScopedAsync(ScopedForUser(key));

    private PluginSecretRecord Read() =>
        configuration.GetConfiguration<PluginSecretRecord>() ?? new();
}

public class PluginSecretRecord
{
    /// <summary>Protected values by scoped key. Never holds a plaintext secret.</summary>
    public Dictionary<string, string> Values { get; init; } = [];
}
