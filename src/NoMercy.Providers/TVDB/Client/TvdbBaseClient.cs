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
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.TVDB.Models.Auth;
using NoMercy.Providers.TVDB.Models.Shared;
using NoMercy.Setup.Server;
using Serilog.Events;

namespace NoMercy.Providers.TVDB.Client;

public class TvdbBaseClient : ExternalApiClient
{
    private readonly string _language;

    private static TvdbLoginResponse? Token { get; set; }
    private static readonly SemaphoreSlim TokenLock = new(1, 1);

    protected TvdbBaseClient()
    {
        _language = "eng";
    }

    protected TvdbBaseClient(int id, string language = "eng")
    {
        Id = id;
        _language = language;
    }

    // TVDB identifies entities with integer ids, unlike the Guid-based default.
    public new int Id { get; private set; }

    protected override string HttpClientName => HttpClientNames.Tvdb;
    protected override Uri BaseUrl => new("https://api4.thetvdb.com/v4/");

    protected override void LogRequest(string url) => Logger.Tvdb(url, LogEventLevel.Verbose);

    private static int Max(int available, int wanted, int constraint)
    {
        return wanted < available
            ? wanted > constraint
                ? constraint
                : wanted
            : available;
    }

    private static bool IsTokenValid(TvdbLoginResponse? token)
    {
        if (token is null)
            return false;

        // ExpiresAt is set to ~1 month after login. Refresh 5 min early.
        return token.Data.ExpiresAt >= DateTime.UtcNow.AddMinutes(5);
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (IsTokenValid(Token))
            return;

        await TokenLock.WaitAsync();
        try
        {
            if (IsTokenValid(Token))
                return;

            Token = await LoginAsync();
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private async Task<TvdbLoginResponse?> LoginAsync()
    {
        if (string.IsNullOrEmpty(ApiKeyStore.Current.TvdbKey))
        {
            Logger.Tvdb("TVDB API key not configured", LogEventLevel.Warning);
            return null;
        }

        try
        {
            HttpClient loginClient = HttpClientProvider.CreateClient(HttpClientNames.TvdbLogin);
            loginClient.BaseAddress ??= BaseUrl;

            using HttpResponseMessage response = await ProviderQueues
                .For(HttpClientNames.TvdbLogin)
                .Enqueue(
                    async () =>
                    {
                        using JsonContent content = JsonContent.Create(
                            new { apikey = ApiKeyStore.Current.TvdbKey }
                        );
                        using HttpRequestMessage request = new(HttpMethod.Post, "login");
                        request.Content = content;
                        // Owned by LoginAsync after the queue returns; disposed here on retry.
                        HttpResponseMessage reply = await loginClient.SendAsync(request);
                        if (
                            reply.StatusCode == HttpStatusCode.TooManyRequests
                            || (int)reply.StatusCode >= 500
                        )
                        {
                            try
                            {
                                reply.EnsureProviderSuccess();
                            }
                            catch
                            {
                                reply.Dispose();
                                throw;
                            }
                        }
                        return reply;
                    },
                    "login"
                );
            if (!response.IsSuccessStatusCode)
            {
                Logger.Tvdb(
                    $"TVDB login failed: {(int)response.StatusCode} {response.ReasonPhrase}",
                    LogEventLevel.Error
                );
                return null;
            }
            string body = await response.Content.ReadAsStringAsync();
            TvdbLoginResponse? login = body.FromJson<TvdbLoginResponse>();
            if (login is not null)
                login.Data.ExpiresAt = DateTime.UtcNow.AddMonths(1);
            return login;
        }
        catch (Exception ex)
        {
            Logger.Tvdb($"TVDB login error: {ex.Message}", LogEventLevel.Error);
            return null;
        }
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
        await EnsureAuthenticatedAsync();
        if (Token is null)
            return null;

        query ??= new();
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
                            nameof(TvdbBaseClient),
                            "Cannot access a disposed TVDB client."
                        );
                    return SendAuthorizedAsync(newUrl);
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
            Logger.Tvdb($"TVDB client disposed during {newUrl}", LogEventLevel.Debug);
            return null;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode
                    is HttpStatusCode.NotFound
                        or HttpStatusCode.BadRequest
                        or HttpStatusCode.UnprocessableEntity
            )
        {
            Logger.Tvdb($"HTTP {ex.StatusCode} for {newUrl}", LogEventLevel.Debug);
            return null;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.TooManyRequests
                || (int?)ex.StatusCode is >= 500 and <= 599
            )
        {
            Logger.Tvdb($"TVDB unavailable after retries for {newUrl}", LogEventLevel.Warning);
            return null;
        }
    }

    private async Task<string> SendAuthorizedAsync(string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (Token is not null)
            request.Headers.Authorization = new("Bearer", Token.Data.Token);
        if (!string.IsNullOrEmpty(_language))
            request.Headers.Add("Accept-Language", _language);

        using HttpResponseMessage response = await Client.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Token rotated/expired mid-flight — clear and let the next call re-login.
            Token = null;
        }

        response.EnsureProviderSuccess();
        return await response.Content.ReadAsStringAsync();
    }

    protected async Task<List<T>?> Paginated<T>(
        string url,
        int limit,
        Dictionary<string, string?>? query = null
    )
        where T : class
    {
        List<T> list = [];
        Dictionary<string, string?> first = query is null ? new() : new(query);
        TvdbPaginatedResponse<T>? page = await Get<TvdbPaginatedResponse<T>>(url, first);
        if (page is null)
            return list;

        list.AddRange(page.Data ?? []);
        int pages = 1;
        while (!string.IsNullOrEmpty(page?.Links?.Next) && pages < Max(int.MaxValue, limit, 500))
        {
            Dictionary<string, string?> next = query is null ? new() : new(query);
            next["page"] = (++pages).ToString();
            page = await Get<TvdbPaginatedResponse<T>>(url, next);
            if (page is null)
                break;

            list.AddRange(page.Data ?? []);
        }

        return list;
    }
}
