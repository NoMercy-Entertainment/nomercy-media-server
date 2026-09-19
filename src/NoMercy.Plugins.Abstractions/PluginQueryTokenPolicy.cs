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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Whether a plugin route still answers a caller that put its bearer token in
/// the URL.
/// <para>
/// Plugin routes carry the server's own authorization now, and a token in a
/// URL is on its way out with it. Design section 10 item 18 says v3 ships the
/// replacement capabilities before v2 code outside the contract is blocked, so
/// a plugin built against ABI 10 keeps being served and is told, once, what to
/// change. From ABI 11 the same request is refused.
/// </para>
/// </summary>
public static class PluginQueryTokenPolicy
{
    /// <summary>The ABI major from which a token in the URL is refused.</summary>
    public const int RefusedFromAbiMajor = 11;

    public const string Why =
        "A token in a URL is written to proxy logs, caches and browser history, so it leaks the "
        + "caller's session to everything on the path.";

    public const string Fix =
        "Send the token in the Authorization header, or serve the stream through "
        + "context.Media.Proxy (capability media.proxy), which asks the host for a short-lived "
        + "user-bound URL. Docs: /plugins/capabilities/media-proxy";

    /// <summary>
    /// Whether the token is still accepted for a plugin built against
    /// <paramref name="targetAbi"/>. A manifest that names no ABI, or names one
    /// the platform cannot read, is a v2-era manifest and is accepted: refusing
    /// it would break a plugin for a line it never wrote.
    /// </summary>
    public static bool Accepts(string? targetAbi)
    {
        if (string.IsNullOrWhiteSpace(targetAbi))
            return true;

        if (!Version.TryParse(targetAbi, out Version? requested))
            return true;

        return requested.Major < RefusedFromAbiMajor;
    }

    public static string What(string pluginName) =>
        $"A request to {pluginName}'s REST route carried its bearer token in the query string.";

    /// <summary>The one line the server log carries while the token is still served.</summary>
    public static string Warning(string pluginName) =>
        $"{What(pluginName)} {Why} Accepted on ABI {RefusedFromAbiMajor - 1}.x and refused from "
        + $"ABI {RefusedFromAbiMajor}: {Fix}";
}
