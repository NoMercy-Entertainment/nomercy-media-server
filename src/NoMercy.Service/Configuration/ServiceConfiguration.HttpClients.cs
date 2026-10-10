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

using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.Providers.Helpers;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    private static void ConfigureHttpClients(IServiceCollection services)
    {
        TimeSpan defaultTimeout = TimeSpan.FromMinutes(5);

        IServerConfiguration config = services
            .BuildServiceProvider()
            .GetRequiredService<IServerConfiguration>();
        string userAgent = config.UserAgent;

        services.AddHttpClient(
            HttpClientNames.Tmdb,
            client =>
            {
                client.BaseAddress = new("https://api.themoviedb.org/3/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.TmdbImage,
            client =>
            {
                client.BaseAddress = new("https://image.tmdb.org/t/p/");
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Accept", "image/*");
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.Tvdb,
            client =>
            {
                client.BaseAddress = new("https://api4.thetvdb.com/v4/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.TvdbLogin,
            client =>
            {
                client.BaseAddress = new("https://api4.thetvdb.com/v4/");
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            }
        );

        services.AddHttpClient(
            HttpClientNames.MusicBrainz,
            client =>
            {
                client.BaseAddress = new("https://musicbrainz.org/ws/2/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                // A blank/generic UA (including the literal string "anonymous")
                // lands in MusicBrainz's shared 50 req/s bucket — not a per-client
                // allowance, but one pool split across every anonymous client on
                // the internet, so our own request pacing can't stop it from
                // getting exhausted by other people's traffic. A properly
                // identified UA bypasses UA-based throttling entirely and only
                // has to respect the 1 req/sec-per-IP limit our Queue already
                // paces to (MusicBrainzBaseClient.RequestIntervalMs).
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            }
        );

        services.AddHttpClient(
            HttpClientNames.AcoustId,
            client =>
            {
                client.BaseAddress = new("https://api.acoustid.org/v2/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            }
        );

        services.AddHttpClient(
            HttpClientNames.OpenSubtitles,
            client =>
            {
                client.BaseAddress = new("https://api.opensubtitles.org/xml-rpc");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("text/xml"));
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    ExternalServicesConfig.Current.OpenSubtitlesUserAgent
                );
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.OpenSubtitlesDownload,
            client =>
            {
                client.DefaultRequestHeaders.Add(
                    "User-Agent",
                    ExternalServicesConfig.Current.OpenSubtitlesUserAgent
                );
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.FanArt,
            client =>
            {
                client.BaseAddress = new("https://webservice.fanart.tv/v3/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            }
        );

        services.AddHttpClient(
            HttpClientNames.FanArtImage,
            client =>
            {
                client.BaseAddress = new("https://assets.fanart.tv");
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Accept", "image/*");
            }
        );

        services.AddHttpClient(
            HttpClientNames.CoverArt,
            client =>
            {
                client.BaseAddress = new("https://coverartarchive.org/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
            }
        );

        services.AddHttpClient(
            HttpClientNames.CoverArtImage,
            client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Accept", "image/*");
            }
        );

        // Lyrics providers sit behind a synchronous /lyrics HTTP request from the
        // client (LyricsResolver), so a hanging call must fail fast rather than
        // inherit HttpClient's 100s default -- LyricsAggregator's own per-stage
        // WaitAsync bound gives up on the caller side well before this, but the
        // request itself still needs to actually stop so the provider's
        // rate-limited Queue slot is freed instead of held for the full 100s.
        TimeSpan lyricsProviderTimeout = TimeSpan.FromSeconds(15);

        services.AddHttpClient(
            HttpClientNames.Lrclib,
            client =>
            {
                client.BaseAddress = new("https://lrclib.net/api/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = lyricsProviderTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.MusixMatch,
            client =>
            {
                client.BaseAddress = new("https://apic-desktop.musixmatch.com/ws/1.1/");
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("authority", "apic-desktop.musixmatch.com");
                client.DefaultRequestHeaders.Add("cookie", "x-mxm-token-guid=");
                client.Timeout = lyricsProviderTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.Tadb,
            client =>
            {
                client.DefaultRequestHeaders.Accept.Clear();
                client.DefaultRequestHeaders.Accept.Add(new("application/json"));
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.NoMercyImage,
            client =>
            {
                client.BaseAddress = new("https://image.nomercy.tv/");
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.DefaultRequestHeaders.Add("Accept", "image/*");
                client.Timeout = defaultTimeout;
            }
        );

        services.AddHttpClient(
            HttpClientNames.AniList,
            client =>
            {
                client.BaseAddress = new("https://graphql.anilist.co/");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
        );

        services.AddHttpClient(
            HttpClientNames.Jikan,
            client =>
            {
                client.BaseAddress = new("https://api.jikan.moe/v4/");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            }
        );

        services.AddHttpClient(
            HttpClientNames.General,
            client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = defaultTimeout;
            }
        );

        // Keycloak (setup/boot auth flows: token exchange, refresh, device-code
        // grant, reachability probe). No BaseAddress — AuthBaseUrl can change at
        // runtime and every caller already builds the full endpoint URL. A bare
        // `new HttpClient()` per call had no timeout, so a stalled IdP hung each
        // request for the BCL's 100s default inside a setup/boot loop.
        services.AddHttpClient(
            ExternalServicesConfig.KeycloakHttpClientName,
            client =>
            {
                client.DefaultRequestHeaders.Add("User-Agent", userAgent);
                client.Timeout = TimeSpan.FromSeconds(15);
            }
        );
    }
}
