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

using System.Text.RegularExpressions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Network;

public class PluginNetworkDeniedException(string host)
    : Exception($"Plugin network access to host '{host}' is not permitted by its capabilities.");

/// <summary>
/// The one place outbound plugin traffic is checked.
/// <para>
/// It reads the union of the manifest's static hosts and whatever the owner has
/// granted since. The manifest can only name what was known at package time,
/// and a plugin whose endpoints are user configuration has nothing honest to
/// put there — so the enforcement point stays exactly here and the list behind
/// it is allowed to grow, rather than the plugin being pushed into building its
/// own <c>HttpClient</c> and leaving the trust model altogether.
/// </para>
/// </summary>
public class PluginNetworkAllowlistHandler : DelegatingHandler
{
    private readonly IReadOnlyList<string> _manifestHosts;
    private readonly Func<IReadOnlyList<string>>? _grantedHosts;

    public PluginNetworkAllowlistHandler(
        IReadOnlyList<string> allowedHosts,
        Func<IReadOnlyList<string>>? grantedHosts = null
    )
    {
        _manifestHosts = allowedHosts;

        // Read through a delegate rather than captured up front: a grant made
        // after the plugin started has to take effect without a restart, or the
        // owner grants a host and nothing happens.
        _grantedHosts = grantedHosts;
    }

    /// <summary>
    /// Compiles one host pattern.
    /// <para>
    /// <c>*</c> matches within a single label and <c>**</c> crosses dots, which
    /// is the distinction the old single-glob form could not make:
    /// <c>*.example.com</c> matched <c>a.example.com</c> but not
    /// <c>a.b.example.com</c>, and a bare <c>*</c> matched only a hostname with
    /// no dots in it at all — so there was no way to write "any host", and the
    /// only way to talk to one was to leave.
    /// </para>
    /// </summary>
    internal static Regex ToPattern(string host) => PluginScopeGlob.ToPattern(host);

    private bool IsAllowed(string host)
    {
        if (PluginScopeGlob.AnyMatches(_manifestHosts, host))
            return true;

        IReadOnlyList<string> granted = _grantedHosts?.Invoke() ?? [];
        return PluginScopeGlob.AnyMatches(granted, host);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string host = request.RequestUri?.Host ?? string.Empty;
        if (!IsAllowed(host))
            throw new PluginNetworkDeniedException(host);

        return base.SendAsync(request, cancellationToken);
    }
}
