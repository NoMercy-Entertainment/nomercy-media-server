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
using NoMercy.Providers.Helpers;
using NoMercy.Providers.Jikan.Models;

namespace NoMercy.Providers.Jikan;

public class JikanMetadataProvider : ExternalApiClient, IJikanMetadataProvider
{
    // Jikan's documented cap is 3 req/s / 60 req/min. Exposed as a constructor
    // parameter, configured from "Providers:Jikan:RequestIntervalMs" via
    // IConfiguration in ServiceConfiguration.Core.cs, so the interval is
    // changeable without a code change.
    private readonly int _requestIntervalMs;

    public JikanMetadataProvider(int requestIntervalMs = 350)
    {
        _requestIntervalMs = requestIntervalMs;
        _ = RequestQueue;
    }

    protected override string HttpClientName => HttpClientNames.Jikan;
    protected override Uri BaseUrl => new("https://api.jikan.moe/v4/");

    protected override int RequestIntervalMs => _requestIntervalMs;

    // Test-only seam: RequestIntervalMs is protected on ExternalApiClient and an
    // override can't widen that, so tests (NoMercy.Tests.Providers has
    // InternalsVisibleTo) read the configured interval through this instead.
    internal int RequestIntervalMsForTesting => RequestIntervalMs;

    protected override bool ShouldSoftFail(System.Net.HttpStatusCode? status) =>
        status is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.TooManyRequests;

    public async Task<JikanAnime?> SearchAsync(string title, int? year, bool? priority = false)
    {
        return await JikanClient.SearchWithPriorityAsync(Client, title, year, priority);
    }

    public async Task<JikanAnime?> GetByIdAsync(int malId, bool? priority = false)
    {
        return await JikanClient.GetByIdWithPriorityAsync(Client, malId, priority);
    }
}
