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
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.AcoustId.Models;
using NoMercy.Providers.Helpers;
using Serilog.Events;

namespace NoMercy.Providers.AcoustId.Client;

public class AcoustIdBaseClient : ExternalApiClient
{
    protected AcoustIdBaseClient() { }

    protected AcoustIdBaseClient(Guid id)
        : base(id) { }

    protected override string HttpClientName => HttpClientNames.AcoustId;
    protected override Uri BaseUrl => new("https://api.acoustid.org/v2/");

    protected override void LogRequest(string url) => Logger.AcoustId(url, LogEventLevel.Verbose);

    // AcoustId-specific fetch: only returns a fingerprint that actually carries
    // recordings (a 200 with no recordings counts as "no result"). This needs
    // the AcoustIdFingerprint constraint, so it cannot reuse the base Get.
    // Transient failures are retried by the shared Queue, not here.
    protected async Task<T?> GetFingerprint<T>(
        string url,
        Dictionary<string, string?>? query = null,
        bool? priority = false
    )
        where T : AcoustIdFingerprint
    {
        query ??= new();
        string newUrl = QueryHelpers.AddQueryString(url, query);

        (bool found, T? cached) = await CacheController.ReadAsync<T>(newUrl);
        if (found && HasRecordings(cached))
            return cached;

        LogRequest(BaseUrl + newUrl);

        try
        {
            // POST the parameters as a form rather than hanging them off the URL. A
            // chromaprint fingerprint is thousands of characters and grows with track
            // length, so the GET form worked for short tracks and returned
            // 414 (URI Too Long) for the rest — a per-track failure that looked like a
            // fingerprinting fault. The cache key stays the composed URL so a hit still
            // matches; only the wire format changes.
            string response = await RequestQueue.Enqueue(
                () => PostFormAsync(url, query),
                newUrl,
                priority
            );
            await CacheController.Write(newUrl, response);
            T? data = response.FromJson<T>();
            return HasRecordings(data) ? data : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode == HttpStatusCode.TooManyRequests
                || (int?)ex.StatusCode is >= 500 and <= 599
            )
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the lookup parameters as an <c>application/x-www-form-urlencoded</c> body.
    /// The <c>meta</c> selector rides along in the path's own query string, which is
    /// short and fixed; only the fingerprint is large enough to matter.
    /// </summary>
    private async Task<string> PostFormAsync(string url, Dictionary<string, string?> query)
    {
        using FormUrlEncodedContent content = new(
            query
                .Where(parameter => parameter.Value is not null)
                .Select(parameter => new KeyValuePair<string, string>(
                    parameter.Key,
                    parameter.Value!
                ))
        );

        using HttpResponseMessage response = await Client.PostAsync(url, content);
        response.EnsureProviderSuccess();

        return await response.Content.ReadAsStringAsync();
    }

    private static bool HasRecordings<T>(T? data)
        where T : AcoustIdFingerprint
    {
        return data?.Results.Length > 0
            && data.Results.Any(fpResult =>
                fpResult.Recordings is not null
                && fpResult.Recordings.Any(recording => recording?.Title != null)
            );
    }
}
