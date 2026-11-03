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
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.NmSystem.SystemCalls;

/// <summary>
/// GitHub announces further pages of a list in the <c>Link</c> header, as
/// <c>&lt;url&gt;; rel="next"</c> next to <c>rel="last"</c>, and sends
/// <c>rel="prev"</c> and <c>rel="first"</c> only on the last page.
/// </summary>
[Trait("Category", "Unit")]
public class GithubReleaseListTests
{
    private const string Releases = "https://api.github.com/repositories/1/releases?per_page=100";

    private static HttpResponseHeaders HeadersWith(string? link)
    {
        HttpResponseMessage response = new();
        if (link is not null)
            response.Headers.Add("Link", link);
        return response.Headers;
    }

    [Fact]
    public void NextPage_IsTheNextRelation_NotTheLast()
    {
        string? next = GithubReleaseList.NextPageUrl(
            HeadersWith($"<{Releases}&page=2>; rel=\"next\", <{Releases}&page=7>; rel=\"last\"")
        );

        next.Should().Be($"{Releases}&page=2");
    }

    [Fact]
    public void LastPage_OffersOnlyPrevAndFirst_SoThereIsNoNext()
    {
        string? next = GithubReleaseList.NextPageUrl(
            HeadersWith($"<{Releases}&page=6>; rel=\"prev\", <{Releases}&page=1>; rel=\"first\"")
        );

        next.Should().BeNull();
    }

    [Fact]
    public void SinglePage_HasNoLinkHeader()
    {
        GithubReleaseList.NextPageUrl(HeadersWith(null)).Should().BeNull();
    }
}
