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

namespace NoMercy.NmSystem.SystemCalls;

/// <summary>
/// Reads GitHub's release list a page at a time. Every dev push adds a nightly
/// prerelease, so the newest beta or stable can sit pages behind the first one;
/// a reader that stops at the first page then finds nothing and never offers an
/// update. Pages follow the <c>Link: rel="next"</c> header GitHub sends while
/// more releases exist, up to <see cref="MaxPages"/>, so a repository with
/// thousands of nightlies cannot keep a boot waiting on GitHub.
/// </summary>
public static class GithubReleaseList
{
    /// <summary>The most releases GitHub returns per page.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// How many pages a single lookup may read. Ten pages of a hundred reach
    /// back further than any channel has ever gone without a release.
    /// </summary>
    public const int MaxPages = 10;

    /// <summary>
    /// Fetches pages starting at <paramref name="firstPageUrl"/> until
    /// <paramref name="pick"/> returns a release, the list ends, or
    /// <see cref="MaxPages"/> pages were read.
    /// </summary>
    /// <returns>The picked release, or null when no page offered one.</returns>
    public static async Task<T?> FindAsync<T>(
        HttpClient httpClient,
        string firstPageUrl,
        Func<string, T[]> parsePage,
        Func<T[], T?> pick
    )
        where T : class
    {
        string? url = firstPageUrl;

        for (int page = 1; page <= MaxPages && url is not null; page++)
        {
            using HttpResponseMessage response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            T? found = pick(parsePage(json));
            if (found is not null)
                return found;

            url = NextPageUrl(response.Headers);
        }

        return null;
    }

    /// <summary>
    /// The <c>rel="next"</c> target of the <c>Link</c> header, or null on the
    /// last page. GitHub sends, for example,
    /// <c>&lt;https://api.github.com/...?page=2&gt;; rel="next", &lt;...&gt;; rel="last"</c>.
    /// </summary>
    public static string? NextPageUrl(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out IEnumerable<string>? values))
            return null;

        foreach (string value in values)
        foreach (string link in value.Split(','))
        {
            string[] parts = link.Split(';');
            if (parts.Length < 2 || !parts.Skip(1).Any(IsNextRelation))
                continue;

            string target = parts[0].Trim();
            if (target.StartsWith('<') && target.EndsWith('>'))
                return target[1..^1];
        }

        return null;
    }

    private static bool IsNextRelation(string parameter)
    {
        string trimmed = parameter.Trim();
        return trimmed.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("rel=next", StringComparison.OrdinalIgnoreCase);
    }
}
