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

using System.Net;
using System.Text.RegularExpressions;
using NoMercy.PluginSdk.Capabilities;

namespace NoMercy.PluginSdk.Network;

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

    private const int MaxRedirects = 10;

    // Redirects are followed here, not by the socket handler, so every hop
    // passes the same host check as the first request.
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        HttpRequestMessage current = request;
        for (int hop = 0; ; hop++)
        {
            string host = current.RequestUri?.Host ?? string.Empty;
            if (!IsAllowed(host))
                throw new PluginNetworkDeniedException(host);

            // Owned by the caller once returned; a redirect hop disposes it here.
            HttpResponseMessage response = await base.SendAsync(current, cancellationToken);
            response.RequestMessage = current;

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            if (hop >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"Too many redirects (limit {MaxRedirects}).");
            }

            Uri target = location.IsAbsoluteUri ? location : new(current.RequestUri!, location);
            if (
                current.RequestUri!.Scheme == Uri.UriSchemeHttps
                && target.Scheme == Uri.UriSchemeHttp
            )
                return response;

            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            current = Follow(current, status, target);
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found
                or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage Follow(
        HttpRequestMessage previous,
        HttpStatusCode status,
        Uri target
    )
    {
        bool toGet =
            status == HttpStatusCode.SeeOther
            || (
                status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                && previous.Method == HttpMethod.Post
            );
        bool hostChanged = !string.Equals(
            previous.RequestUri?.Host,
            target.Host,
            StringComparison.OrdinalIgnoreCase
        );

        HttpRequestMessage next = new(toGet ? HttpMethod.Get : previous.Method, target)
        {
            Version = previous.Version,
            VersionPolicy = previous.VersionPolicy,
        };
        if (!toGet)
            next.Content = previous.Content;

        foreach (KeyValuePair<string, IEnumerable<string>> header in previous.Headers)
        {
            if (
                hostChanged
                && (
                    header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;

            next.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (KeyValuePair<string, object?> option in previous.Options)
            next.Options.Set(new(option.Key), option.Value);

        return next;
    }
}
