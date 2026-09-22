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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What a plugin may ask the owner for after it is installed.
/// <para>
/// A manifest can only name what is known at package time. A plugin whose
/// remote hosts are user configuration — an indexer, a client, whatever the
/// user typed in — has nothing it can honestly put there. The two available
/// answers were both bad: wildcard everything, or build a plain
/// <c>HttpClient</c> and step around the allowlist entirely. The second one
/// works, which is the problem.
/// </para>
/// <para>
/// So a plugin asks at runtime, the owner sees the request in the dashboard and
/// decides, and the enforcement point does not move. The plugin gets a usable
/// path that stays inside the trust model instead of a reason to leave it.
/// </para>
/// </summary>
public interface IPluginGrants
{
    /// <summary>Whether this plugin currently holds <paramref name="value"/> for <paramref name="kind"/>.</summary>
    Task<bool> HasAsync(string kind, string value, CancellationToken ct = default);

    /// <summary>Everything this plugin holds of one kind.</summary>
    Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default);

    /// <summary>
    /// Asks the owner for <paramref name="value"/>, surfacing in the dashboard
    /// with <paramref name="reason"/> as the explanation shown to them.
    /// <para>Returns immediately. It records a request; it does not wait for an
    /// answer and never blocks a plugin on a human. Asking twice for the same
    /// thing is not an error and does not queue a second prompt.</para>
    /// </summary>
    Task RequestAsync(string kind, string value, string reason, CancellationToken ct = default);
}

/// <param name="Kind">One of <see cref="PluginGrantKind"/>.</param>
/// <param name="Reason">The plugin's own words, shown to the owner. Treated as untrusted text.</param>
public record PluginGrantRequest(
    Ulid PluginId,
    string Kind,
    string Value,
    string Reason,
    DateTime RequestedAt
);
