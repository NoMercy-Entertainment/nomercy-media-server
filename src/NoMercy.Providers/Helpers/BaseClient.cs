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

using Microsoft.AspNetCore.WebUtilities;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Providers.Helpers;

public class BaseClient : IDisposable
{
    protected Guid Id { get; set; }

    protected readonly HttpClient Client;

    protected virtual Uri BaseUrl => new("http://localhost:8080");
    protected virtual int ConcurrentRequests => 1;
    protected virtual int Interval => 1000;
    protected virtual Dictionary<string, string?> QueryParams => new();
    protected virtual string UserAgent => ExternalServicesConfig.Current.UserAgent;

    protected BaseClient()
    {
        Client = HttpClientProvider.CreateClient(HttpClientNames.General);
        Client.BaseAddress = BaseUrl;
        Client.Timeout = TimeSpan.FromMinutes(5);

        foreach ((string? key, string? value) in QueryParams)
            Client.DefaultRequestHeaders.Add(key, value);
    }

    protected BaseClient(Guid id)
    {
        Id = id;
        Client = HttpClientProvider.CreateClient(HttpClientNames.General);
        Client.BaseAddress = BaseUrl;
        Client.Timeout = TimeSpan.FromMinutes(5);

        foreach ((string? key, string? value) in QueryParams)
            Client.DefaultRequestHeaders.Add(key, value);
    }

    protected static Queue Queue() => ProviderQueues.For(HttpClientNames.General);

    protected virtual async Task<T?> Get<T>(
        string url,
        Dictionary<string, string?>? query,
        bool? priority = false
    )
        where T : class
    {
        query ??= new();

        foreach (KeyValuePair<string, string?> queryParam in QueryParams)
            query.Add(queryParam.Key, queryParam.Value);

        string newUrl = QueryHelpers.AddQueryString(url, query);

        (bool found, T? result) = await CacheController.ReadAsync<T>(newUrl);
        if (found)
            return result;

        Logger.Http(newUrl, LogEventLevel.Verbose);

        string response = await Queue()
            .Enqueue(() => ProviderHttp.GetStringAsync(Client, newUrl), newUrl, priority);

        await CacheController.Write(newUrl, response);

        T? data = response.FromJson<T>();

        return data;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
