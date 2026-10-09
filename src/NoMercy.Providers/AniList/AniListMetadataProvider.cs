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

using NoMercy.Providers.Abstractions;
using NoMercy.Providers.AniList.Models;
using NoMercy.Providers.Helpers;

namespace NoMercy.Providers.AniList;

public class AniListMetadataProvider : ExternalApiClient, IAniListMetadataProvider
{
    // AniList's public rate limit is currently degraded to ~30 req/min (was 90)
    // while they rebuild anti-abuse tooling. Exposed as a constructor parameter,
    // configured from "Providers:AniList:RequestIntervalMs" via IConfiguration
    // in ServiceConfiguration.Core.cs, so the interval is changeable without a
    // code change.
    private readonly int _requestIntervalMs;

    public AniListMetadataProvider(int requestIntervalMs = 2000)
    {
        _requestIntervalMs = requestIntervalMs;
        _ = RequestQueue;
    }

    protected override string HttpClientName => HttpClientNames.AniList;
    protected override Uri BaseUrl => new("https://graphql.anilist.co/");

    protected override int RequestIntervalMs => _requestIntervalMs;

    // Test-only seam: RequestIntervalMs is protected on ExternalApiClient and an
    // override can't widen that, so tests (NoMercy.Tests.Providers has
    // InternalsVisibleTo) read the configured interval through this instead.
    internal int RequestIntervalMsForTesting => RequestIntervalMs;

    protected override bool ShouldSoftFail(System.Net.HttpStatusCode? status) =>
        status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.TooManyRequests;

    public async Task<AniListMedia?> SearchAsync(string title, int? year, bool? priority = false)
    {
        return await AniListClient.SearchWithPriorityAsync(Client, title, year, priority);
    }
}
