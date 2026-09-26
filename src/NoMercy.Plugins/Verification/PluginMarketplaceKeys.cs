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

using System.Text.Json;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// The NoMercy marketplace keys this server trusts.
/// <para>
/// Three layers, later ones winning: the public keys this build shipped with,
/// the set nomercy.tv publishes at <c>/v1/marketplace/keys.json</c>, and the
/// owner's own <c>Plugins:TrustedKeys</c>. The shipped keys mean a server
/// with no network still checks signatures. A published key marked
/// <c>"revoked": true</c> is dropped, shipped or not; a key the set simply
/// omits is kept, because old keys stay published after a rotation and a
/// missing one is more likely a short answer than a withdrawal.
/// </para>
/// </summary>
public sealed class PluginMarketplaceKeys : IPluginTrustedKeys
{
    /// <summary>Public halves as published on 2026-09-26. Public keys, not secrets.</summary>
    public static IReadOnlyDictionary<string, string> Shipped { get; } =
        new Dictionary<string, string>
        {
            ["mk_2026_09c"] = "UbpdyF8xAjmWCWF0YWEOhfXO6OIUBcr39VsNw8jMiKQ=",
            ["mk_2026_09b"] = "XY/C4NlknNsxIjkhXMKtv3amk6kqlrGYe/kTc0uU/ro=",
            ["mk_2026_09"] = "8llIlaK+E9+/rLiK246qcOWckL0m0Rs+F65SbPIaDu4=",
        };

    private readonly IReadOnlyDictionary<string, string> _configured;
    private readonly Dictionary<string, string> _published = new();
    private readonly HashSet<string> _revoked = [];
    private readonly Lock _gate = new();
    private IReadOnlyDictionary<string, string> _current;

    public PluginMarketplaceKeys(IReadOnlyDictionary<string, string> configured)
    {
        _configured = configured;
        _current = Merge();
    }

    public string? Find(string keyId) => _current.TryGetValue(keyId, out string? key) ? key : null;

    public bool Any => _current.Count > 0;

    /// <summary>
    /// Takes a published key set. False, and nothing changed, when the body is
    /// not one. Fields this server does not read are ignored.
    /// </summary>
    public bool Apply(string body)
    {
        Dictionary<string, string> published = new();
        HashSet<string> revoked = [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("keys", out JsonElement keys)
                || keys.ValueKind != JsonValueKind.Array
            )
                return false;

            foreach (JsonElement key in keys.EnumerateArray())
            {
                if (
                    key.ValueKind != JsonValueKind.Object
                    || !key.TryGetProperty("kid", out JsonElement kid)
                    || kid.GetString() is not { Length: > 0 } keyId
                )
                    continue;

                if (
                    key.TryGetProperty("revoked", out JsonElement flag)
                    && flag.ValueKind == JsonValueKind.True
                )
                {
                    revoked.Add(keyId);
                    continue;
                }

                if (
                    key.TryGetProperty("public_key", out JsonElement publicKey)
                    && publicKey.GetString() is { Length: > 0 } value
                )
                    published[keyId] = value;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        lock (_gate)
        {
            foreach (KeyValuePair<string, string> key in published)
                _published[key.Key] = key.Value;

            _revoked.UnionWith(revoked);
            _current = Merge();
        }

        return true;
    }

    private Dictionary<string, string> Merge()
    {
        Dictionary<string, string> merged = new(Shipped);

        foreach (KeyValuePair<string, string> key in _published)
            merged[key.Key] = key.Value;

        foreach (string keyId in _revoked)
            merged.Remove(keyId);

        // The owner's own keys last: an explicit choice on this server is not
        // something a network answer should overrule.
        foreach (KeyValuePair<string, string> key in _configured)
            merged[key.Key] = key.Value;

        return merged;
    }
}
