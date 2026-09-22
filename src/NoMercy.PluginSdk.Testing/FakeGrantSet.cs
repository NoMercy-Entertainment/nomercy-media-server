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

namespace NoMercy.PluginSdk.Testing;

/// <summary>
/// What the owner granted, in a test.
/// <para>
/// A capability with no scopes is granted for everything of its kind; one with
/// scopes is granted only for those. That is the host's own rule rather than a
/// convenience, so a plugin that passes here behaves the same on a server.
/// </para>
/// </summary>
public sealed class FakeGrantSet
{
    private readonly Dictionary<string, HashSet<string>> _granted = new(StringComparer.Ordinal);

    public void Grant(string capability, params string[] scopes)
    {
        if (!_granted.TryGetValue(capability, out HashSet<string>? existing))
        {
            existing = new(StringComparer.OrdinalIgnoreCase);
            _granted[capability] = existing;
        }

        foreach (string scope in scopes)
            existing.Add(scope);
    }

    public void Revoke(string capability) => _granted.Remove(capability);

    public bool IsDeclared(string capability) => _granted.ContainsKey(capability);

    public bool Covers(string capability, string scope) =>
        _granted.TryGetValue(capability, out HashSet<string>? scopes)
        && (scopes.Count == 0 || scopes.Contains(scope));
}
