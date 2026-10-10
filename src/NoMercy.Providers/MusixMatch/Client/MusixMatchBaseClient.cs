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
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Helpers;
using NoMercy.Setup.Server;
using Serilog.Events;

namespace NoMercy.Providers.MusixMatch.Client;

public class MusixMatchBaseClient : ExternalApiClient
{
    protected MusixMatchBaseClient() { }

    protected MusixMatchBaseClient(Guid id)
        : base(id) { }

    protected override string HttpClientName => HttpClientNames.MusixMatch;
    protected override Uri BaseUrl => new("https://apic-desktop.musixmatch.com/ws/1.1/");

    protected override void LogRequest(string url) => Logger.MusixMatch(url, LogEventLevel.Verbose);

    // Fixed, non-secret parameters required on every call. Safe to log/cache.
    protected override void AugmentQuery(Dictionary<string, string?> query)
    {
        query["format"] = "json";
        query["namespace"] = "lyrics_richsynched";
        query["subtitle_format"] = "mxm";
        query["app_id"] = "web-desktop-app-v1.0";
    }

    // The rolling user token is a secret: MusixMatch's API only accepts it as a
    // query parameter (it is not honoured as a header), so it is injected at
    // request time and deliberately kept out of cache filenames and the request
    // log, which previously leaked it on every call.
    protected override void AddSecretQuery(Dictionary<string, string?> query)
    {
        query["usertoken"] = ApiKeyStore.Current.MusixmatchKey;
    }

    // MusixMatch returns 401 when its rolling user token rotates; soft-fail so
    // callers can fall through to other lyric sources.
    protected override bool ShouldSoftFail(HttpStatusCode? status) =>
        status
            is HttpStatusCode.NotFound
                or HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized;

    protected override void OnSoftFail(HttpStatusCode? status, string url) =>
        Logger.MusixMatch($"HTTP {status} for {url}", LogEventLevel.Debug);
}
