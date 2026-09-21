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

namespace NoMercy.Plugins.Verification;

/// <summary>
/// The publisher keys this server will accept a signature from.
/// <para>
/// Keyed by the id the signature block names, so a publisher can rotate by
/// publishing under a new key id while old packages still verify against the
/// old one. A single key with no id would make every rotation a flag day for
/// everyone who had not updated yet.
/// </para>
/// </summary>
public interface IPluginTrustedKeys
{
    /// <summary>The raw base64 public key for an id, or null when it is not trusted.</summary>
    string? Find(string keyId);

    /// <summary>Whether this server trusts anybody at all.</summary>
    bool Any { get; }
}

/// <summary>
/// The keys the build shipped with.
/// <para>
/// Empty until the marketplace exists. That emptiness is deliberate and is
/// what the stage reads to tell a signed package it cannot check from an
/// unsigned one it should refuse: a server that trusts nobody must not silently
/// accept everybody.
/// </para>
/// </summary>
public sealed class PluginTrustedKeys(IReadOnlyDictionary<string, string> keys) : IPluginTrustedKeys
{
    public static PluginTrustedKeys None { get; } = new(new Dictionary<string, string>());

    public string? Find(string keyId) => keys.TryGetValue(keyId, out string? key) ? key : null;

    public bool Any => keys.Count > 0;
}
