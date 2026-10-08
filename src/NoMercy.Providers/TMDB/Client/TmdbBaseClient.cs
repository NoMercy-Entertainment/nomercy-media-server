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
using Microsoft.AspNetCore.WebUtilities;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.TMDB.Models.Networks;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Setup.Server;
using Serilog.Events;

namespace NoMercy.Providers.TMDB.Client;

public class TmdbBaseClient : ExternalApiClient
{
    private readonly string Language;

    protected TmdbBaseClient()
    {
        Language = "en,null";
    }

    protected TmdbBaseClient(int id, string language = "en-US")
    {
        Id = id;
        Language = language + ",null";
    }

    // TMDB identifies entities with integer ids, unlike the Guid-based default.
    public new int Id { get; private set; }

    protected override string HttpClientName => HttpClientNames.Tmdb;
    protected override Uri BaseUrl => new("https://api.themoviedb.org/3/");

    protected override void ConfigureClient(HttpClient client) =>
        client.DefaultRequestHeaders.Add(
            "Authorization",
            $"Bearer {ApiKeyStore.Current.TmdbToken}"
        );

    protected override void LogRequest(string url) => Logger.MovieDb(url, LogEventLevel.Verbose);

    private static int Max(int available, int wanted, int constraint)
    {
        return wanted < available
            ? wanted > constraint
                ? constraint
                : wanted
            : available;
    }

    protected override async Task<T?> Get<T>(
        string url,
        Dictionary<string, string?>? query = null,
        bool? priority = false,
        bool skipCache = false,
        TimeSpan? maxCacheAge = null
    )
        where T : class
    {
        query ??= new();
        if (!query.ContainsKey("language"))
            query["language"] = priority is true ? Language : "";
        query["include_adult"] = RuntimeServerSettings.Current.ShowAdultContent ? "true" : "false";

        string newUrl = QueryHelpers.AddQueryString(url, query);

        if (!skipCache)
        {
            (bool found, T? result) = maxCacheAge is { } age
                ? await CacheController.ReadAsync<T>(newUrl, age)
                : await CacheController.ReadAsync<T>(newUrl);
            if (found)
                return result;
        }

        LogRequest(BaseUrl + newUrl);

        try
        {
            string response = await RequestQueue.Enqueue(
                () =>
                {
                    if (Disposed)
                        throw new ObjectDisposedException(
                            nameof(TmdbBaseClient),
                            "Cannot access a disposed TMDB client."
                        );
                    return ProviderHttp.GetStringAsync(Client, newUrl);
                },
                newUrl,
                priority
            );

            if (!skipCache)
                await CacheController.Write(newUrl, response);
            return response.FromJson<T>();
        }
        catch (ObjectDisposedException)
        {
            Logger.MovieDb(
                $"TMDB client disposed during operation for {newUrl}",
                LogEventLevel.Debug
            );
            return null;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode
                    is HttpStatusCode.NotFound
                        or HttpStatusCode.UnprocessableEntity
                        or HttpStatusCode.BadRequest
                        or HttpStatusCode.Forbidden
                        or HttpStatusCode.TooManyRequests
                        or HttpStatusCode.ServiceUnavailable
            )
        {
            Logger.MovieDb($"HTTP {ex.StatusCode} for {newUrl}", LogEventLevel.Debug);
            return null;
        }
        catch (HttpRequestException ex)
        {
            // Any other HTTP failure — an unexpected status, or a transport-level
            // error with no status code (connection reset / timeout under load).
            // Degrade to a null (cache-miss-style) result rather than throwing so
            // callers — and the *HandlesGracefully contract — never see an exception.
            Logger.MovieDb(
                $"TMDB HTTP request failed for {newUrl}: {ex.Message}",
                LogEventLevel.Warning
            );
            return null;
        }
        catch (Exception ex)
            when (ex is Newtonsoft.Json.JsonException or OperationCanceledException)
        {
            Logger.MovieDb(
                $"TMDB response could not be processed for {newUrl}: {ex.Message}",
                LogEventLevel.Warning
            );
            return null;
        }
    }

    protected async Task<List<T>?> Paginated<T>(string url, int limit)
        where T : class
    {
        List<T> list = [];
        TmdbPaginatedResponse<T>? firstPage = await Get<TmdbPaginatedResponse<T>>(url);
        list.AddRange(firstPage?.Results ?? []);
        if (limit > 1)
            await Parallel.ForAsync(
                2,
                Max(firstPage?.TotalPages ?? 0, limit, 500) + 1,
                async (i, _) =>
                {
                    TmdbPaginatedResponse<T>? page = await Get<TmdbPaginatedResponse<T>>(
                        url,
                        new() { ["page"] = i.ToString() }
                    );
                    lock (list)
                    {
                        list.AddRange(page?.Results ?? []);
                    }
                }
            );
        return list;
    }

    public Task<TmdbTmdbNetworkDetails?> CompanyDetails(int id, bool? priority = false)
    {
        return Get<TmdbTmdbNetworkDetails>("company/" + id, priority: priority);
    }
}
