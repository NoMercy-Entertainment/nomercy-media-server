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

using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using NoMercy.NmSystem.Configuration;

namespace NoMercy.Plugins.Network;

/// <summary>
/// Names the plugin behind an outbound request.
/// <para>
/// Two reasons. Several of the services plugins talk to require a descriptive
/// user agent and rate-limit or ban a generic one — MusicBrainz says so
/// outright — and without this a plugin's traffic is indistinguishable from the
/// server's own, so "why is my server hammering this tracker" has no answer in
/// the request.
/// </para>
/// <para>
/// This is attribution, not enforcement. A plugin runs in-process and can build
/// its own <see cref="HttpClient"/>, which is the documented boundary of the
/// trust model. It makes well-behaved traffic identifiable; it does not
/// constrain traffic that is not.
/// </para>
/// </summary>
public partial class PluginUserAgentHandler(
    Ulid pluginId,
    string? pluginName,
    Version? pluginVersion
) : DelegatingHandler
{
    /// <summary>Everything outside an RFC 9110 token, which is most punctuation.</summary>
    [GeneratedRegex("[^A-Za-z0-9!#$%&'*+.^_`|~-]+")]
    private static partial Regex NonTokenCharacters();

    private readonly string _product = BuildProduct(pluginId, pluginName, pluginVersion);

    /// <summary>
    /// The product token for one plugin: its name reduced to token characters,
    /// or its id when the name has none left.
    /// <para>A plugin name is author-supplied text. "My Plugin (Beta)" is not a
    /// product token — the space ends it and the parenthesis opens a comment —
    /// so an unsanitised name is a malformed header, and a name containing a
    /// newline would be worse than malformed.</para>
    /// </summary>
    internal static string BuildProduct(Ulid pluginId, string? name, Version? version)
    {
        string token = NonTokenCharacters().Replace(name ?? string.Empty, "-").Trim('-', '.');

        // Punctuation alone is a valid token and a useless name. "!!!" and
        // "..." survive the filter untouched — every character in them is legal
        // — and attribute a request to nothing. The id is worse to read and
        // actually identifies the plugin.
        if (!token.Any(char.IsLetterOrDigit))
            token = pluginId.ToString();

        return $"NoMercyPlugin-{token}/{version?.ToString() ?? "0.0.0"}";
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        // Always, and replacing whatever the plugin set.
        //
        // This used to defer to a plugin's own user agent, on the reasoning that
        // a private tracker bans on a string it does not expect and an indexer
        // behind a browser check wants a browser one. That reasoning is
        // overturned deliberately: it let a plugin choose the identity this
        // server presents to a third party from the owner's address, which is
        // the plugin picking who we are. Egress to a host we do not control
        // carries the owner's configured identity and the plugin's attribution
        // beside it, and neither is the plugin's to replace.
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(ExternalServicesConfig.Current.UserAgent);
        request.Headers.UserAgent.ParseAdd(_product);

        return base.SendAsync(request, cancellationToken);
    }
}
