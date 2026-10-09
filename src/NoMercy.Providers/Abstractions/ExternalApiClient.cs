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
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Helpers;
using Serilog.Events;

namespace NoMercy.Providers.Abstractions;

/// <summary>
/// Shared base for the HTTP provider clients (TMDB, TVDB, MusicBrainz, FanArt,
/// CoverArt, Lrclib, MusixMatch, …). Captures the boilerplate that was
/// duplicated across every provider base class: a named <see cref="HttpClient"/>
/// from the factory, a per-provider rate-limited <see cref="Queue"/>, dev-only
/// on-disk caching via <see cref="CacheController"/>, soft-fail on configurable
/// error statuses, and IDisposable.
///
/// Transient-failure retries are owned by <see cref="Queue"/> (which retries
/// 502/503/504/429 with exponential backoff); provider clients deliberately do
/// NOT add their own retry loops on top, to avoid retry amplification.
///
/// Concrete providers supply <see cref="HttpClientName"/>, <see cref="BaseUrl"/>
/// and (optionally) their concurrency/interval, and may override
/// <see cref="ConfigureClient"/> for auth headers, <see cref="LogRequest"/> for
/// their provider-specific log channel, <see cref="AugmentQuery"/> to inject
/// fixed query parameters, <see cref="AddSecretQuery"/> to inject secrets that
/// must stay out of cache keys and logs, and <see cref="ShouldSoftFail"/> to
/// tune which error statuses resolve to null. Providers whose request flow is
/// genuinely different (dynamic auth tokens, per-request headers, language
/// injection, …) may override <see cref="Get{T}"/> entirely while still reusing
/// the shared <see cref="Client"/>, <see cref="RequestQueue"/> and caching.
/// </summary>
public abstract class ExternalApiClient : IExternalProvider
{
    // One queue per named HttpClient (i.e. per provider family), shared across
    // every concrete client of that family, so the provider-wide concurrency/
    // interval rate limit is preserved — matching the old per-provider
    // 'static Queue' semantics. Keying by concrete type would instead give each
    // sub-client its own queue and multiply the request rate.

    protected Guid Id { get; private set; }
    protected readonly HttpClient Client;

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    protected bool Disposed { get; private set; }

    protected abstract string HttpClientName { get; }
    protected abstract Uri BaseUrl { get; }
    protected virtual int ConcurrentRequests =>
        ProviderQueues.OptionsFor(HttpClientName).Concurrent;
    protected virtual int RequestIntervalMs => ProviderQueues.OptionsFor(HttpClientName).Interval;

    protected ExternalApiClient()
    {
        Client = HttpClientProvider.CreateClient(HttpClientName);
        Client.BaseAddress ??= BaseUrl;
        ConfigureClient(Client);
    }

    protected ExternalApiClient(Guid id)
        : this()
    {
        Id = id;
    }

    /// <summary>Per-provider hook to add default headers / auth. Default: no-op.</summary>
    protected virtual void ConfigureClient(HttpClient client) { }

    /// <summary>Per-provider verbose request log. Default: the generic HTTP channel.</summary>
    protected virtual void LogRequest(string url) => Logger.Http(url, LogEventLevel.Verbose);

    /// <summary>
    /// Per-provider hook to inject non-secret query parameters (fixed format
    /// flags, …) into a private copy of the caller's query before the URL is
    /// built. These parameters DO appear in the cache key and the request log.
    /// Default: no-op. The dictionary is already a copy, so implementations may
    /// mutate it freely.
    /// </summary>
    protected virtual void AugmentQuery(Dictionary<string, string?> query) { }

    /// <summary>
    /// Per-provider hook to inject secret query parameters (api keys / tokens)
    /// that must be sent to the upstream API but kept OUT of cache filenames and
    /// request logs. These are appended to the outgoing request URL only, after
    /// the cache key and log line have been computed. Default: no-op.
    /// </summary>
    protected virtual void AddSecretQuery(Dictionary<string, string?> query) { }

    /// <summary>
    /// Whether an HTTP error status should resolve to "no result" (null) rather
    /// than be thrown. Default: 404 and 400.
    /// </summary>
    protected virtual bool ShouldSoftFail(HttpStatusCode? status) =>
        status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest;

    /// <summary>Hook invoked just before a soft-fail resolves to null. Default: no-op.</summary>
    protected virtual void OnSoftFail(HttpStatusCode? status, string url) { }

    protected Queue RequestQueue =>
        ProviderQueues.For(
            HttpClientName,
            new() { Concurrent = ConcurrentRequests, Interval = RequestIntervalMs }
        );

    protected virtual async Task<T?> Get<T>(
        string url,
        Dictionary<string, string?>? query = null,
        bool? priority = false,
        bool skipCache = false,
        TimeSpan? maxCacheAge = null
    )
        where T : class
    {
        // Copy so AugmentQuery / our own additions never mutate the caller's dict.
        Dictionary<string, string?> effectiveQuery = query is null ? new() : new(query);
        AugmentQuery(effectiveQuery);

        // Cache key and log line are built WITHOUT secrets.
        string newUrl = QueryHelpers.AddQueryString(url, effectiveQuery);

        if (!skipCache)
        {
            (bool found, T? result) = maxCacheAge is { } age
                ? await CacheController.ReadAsync<T>(newUrl, age)
                : await CacheController.ReadAsync<T>(newUrl);
            if (found)
                return result;
        }

        LogRequest(BaseUrl + newUrl);

        // Secrets are appended to the outgoing request URL only.
        Dictionary<string, string?> secretQuery = new();
        AddSecretQuery(secretQuery);
        string requestUrl =
            secretQuery.Count == 0 ? newUrl : QueryHelpers.AddQueryString(newUrl, secretQuery);

        try
        {
            // No retry here: Queue.Enqueue already retries transient failures.
            string response = await RequestQueue.Enqueue(
                () => ProviderHttp.GetStringAsync(Client, requestUrl),
                newUrl,
                priority
            );

            if (!skipCache)
                await CacheController.Write(newUrl, response);
            return response.FromJson<T>();
        }
        catch (HttpRequestException ex) when (ShouldSoftFail(ex.StatusCode))
        {
            // Provider signalled "not found" — soft-fail to null.
            OnSoftFail(ex.StatusCode, newUrl);
            return null;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.TooManyRequests
                || (int?)ex.StatusCode is >= 500 and <= 599
            )
        {
            Logger.Http($"Provider unavailable after retries: {newUrl}", LogEventLevel.Warning);
            return null;
        }
    }

    public void Dispose()
    {
        Disposed = true;
        GC.SuppressFinalize(this);
    }
}
