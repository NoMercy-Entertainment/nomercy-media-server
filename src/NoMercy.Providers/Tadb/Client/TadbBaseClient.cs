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

using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using NoMercy.Setup.Server;
using Serilog.Events;

namespace NoMercy.Providers.Tadb.Client;

public class TadbBaseClient : ExternalApiClient
{
    protected TadbBaseClient() { }

    protected TadbBaseClient(int id)
    {
        Id = id;
    }

    // Tadb identifies entities with integer ids, unlike the Guid-based default.
    public new int Id { get; private set; }

    protected override string HttpClientName => HttpClientNames.Tadb;
    protected override Uri BaseUrl => new("https://www.theaudiodb.com/api/v1/json/");
    protected override int ConcurrentRequests => 2;

    // The API key travels as a Bearer header (not a URL path segment) so it
    // never lands in cache filenames or access logs.
    protected override void ConfigureClient(HttpClient client) =>
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            ApiKeyStore.Current.TadbKey
        );

    protected override void LogRequest(string url) => Logger.AudioDb(url, LogEventLevel.Verbose);
}
