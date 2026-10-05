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
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using Serilog.Events;

namespace NoMercy.Providers.MusicBrainz.Client;

public class MusicBrainzBaseClient : ExternalApiClient
{
    protected MusicBrainzBaseClient() { }

    protected MusicBrainzBaseClient(Guid id)
        : base(id) { }

    protected override string HttpClientName => HttpClientNames.MusicBrainz;
    protected override Uri BaseUrl => new("https://musicbrainz.org/ws/2/");

    // MusicBrainz asks clients to stay at roughly one request per second.
    protected override int RequestIntervalMs => 1500;

    // MusicBrainz answers 403 to anonymous callers, so a request without a
    // User-Agent never returns data. The DI-registered client normally carries one,
    // but a caller that resolves it bare (an early seed path constructing the client
    // itself) would otherwise reach the API with no agent at all.
    protected override void ConfigureClient(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.WithNoMercyUserAgent();
    }

    protected override void LogRequest(string url) =>
        Logger.MusicBrainz(url, LogEventLevel.Verbose);

    // MusicBrainz returns 404 for unknown MBIDs — treat as "no result".
    // Transient 429/503 are retried by the shared Queue, not here.
    protected override bool ShouldSoftFail(HttpStatusCode? status) =>
        status is HttpStatusCode.NotFound;
}
