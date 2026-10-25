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

using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.NmSystem.Extensions;

public static partial class Url
{
    // RFC 3986 pchar, minus the percent-encoding introducer: what may stand
    // unescaped inside a URL path segment.
    private const string PathSafePunctuation = "-._~!$&'()*+,;=:@";

    /// <summary>
    /// Percent-encodes a served file path so it survives a URL parser.
    /// </summary>
    /// <remarks>
    /// Library filenames are user data and routinely carry characters a URL
    /// parser reads as structure: <c>#</c> starts a fragment, so everything
    /// after it is dropped from the request, and a bare <c>%</c> that is not
    /// followed by two hex digits is an invalid escape that Cloudflare rejects
    /// with a 400 before the request ever reaches us — which a browser then
    /// reports as a CORS failure with no status.
    /// <para>Only characters that are illegal in a path segment are escaped,
    /// so every path without them stays byte-identical and existing clients,
    /// cached URLs, and the ingest-key prefix match are unaffected. Non-ASCII
    /// is left alone: browsers and Kestrel already agree on its encoding.</para>
    /// </remarks>
    public static string EncodePath(this string path)
    {
        StringBuilder builder = new(path.Length);

        foreach (char character in path)
        {
            if (
                character == '/'
                || character > 127
                || char.IsAsciiLetterOrDigit(character)
                || PathSafePunctuation.Contains(character)
            )
                builder.Append(character);
            else
                builder.Append('%').Append(((int)character).ToString("X2"));
        }

        return builder.ToString();
    }

    public static Uri ToHttps(this Uri url)
    {
        UriBuilder uriBuilder = new(url)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1, // default port for scheme
        };

        return uriBuilder.Uri;
    }

    public static string FileName(this Uri url)
    {
        return Path.GetFileName(url.LocalPath);
    }

    public static string BasePath(this Uri url)
    {
        return url.ToString().Replace("/" + url.FileName(), "");
    }

    public static bool HasSuccessStatus(this Uri url, string? contentType = null)
    {
        try
        {
            HttpClient httpClient = new();
            httpClient.DefaultRequestHeaders.Add(
                "User-Agent",
                "NoMercy wMediaServer/0.1.0 ( admin@nomercy.tv )"
            );

            if (contentType is not null)
                httpClient.DefaultRequestHeaders.Add("Accept", contentType);

            using HttpResponseMessage res = httpClient.SendAsync(new(HttpMethod.Head, url)).Result;
            return res.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static string SafeHost(this string url)
    {
        url = ReplaceIpV4().Replace(url, "-");
        url = ReplaceIpV6().Replace(url, "-");
        return url;
    }

    [GeneratedRegex(":", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReplaceIpV6();

    [GeneratedRegex("\\.", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ReplaceIpV4();
}
