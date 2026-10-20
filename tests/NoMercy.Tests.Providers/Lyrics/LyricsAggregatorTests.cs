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

using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NoMercy.Database.Models.Music;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Providers.Helpers;
using NoMercy.Providers.Lrclib.Models;
using NoMercy.Providers.Lyrics;
using NoMercy.Providers.MusixMatch.Models;

namespace NoMercy.Tests.Providers.Lyrics;

/// <summary>
/// Exercises <see cref="LyricsAggregator"/> end to end (real client, real
/// Queue rate limiter, only the HTTP transport faked) to cover the perf
/// rework: the /get-synced short-circuit still logs its winner, a fast wrong
/// match never beats a slower correct one once both branches race, and a
/// provider that never answers can't stall the whole resolve.
/// </summary>
[Collection("HttpClientProvider")]
[Trait("Category", "Unit")]
public class LyricsAggregatorTests : IDisposable
{
    private ServiceProvider? _serviceProvider;
    private readonly List<LogEntry> _logs = [];

    public LyricsAggregatorTests()
    {
        Logger.LogEmitted += OnLogEmitted;
    }

    public void Dispose()
    {
        Logger.LogEmitted -= OnLogEmitted;
        HttpClientProvider.Reset();
        _serviceProvider?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnLogEmitted(LogEntry entry) => _logs.Add(entry);

    private static Track MakeTrack(string title, string artist, string album, int durationSeconds)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            Name = title,
            Duration = TimeSpan.FromSeconds(durationSeconds).ToString(@"hh\:mm\:ss"),
            ArtistTrack = [new() { Artist = new() { Name = artist } }],
            AlbumTrack = [new() { Album = new() { Name = album } }],
        };
    }

    // Every test uses its own random title/artist so the on-disk provider
    // cache (process-wide, keyed by URL) can never let one test's response
    // leak into another's.
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}";

    private void ConfigureHttp(
        HttpMessageHandler lrclibHandler,
        HttpMessageHandler musixmatchHandler
    )
    {
        ServiceCollection services = new();
        services
            .AddHttpClient(
                HttpClientNames.Lrclib,
                client => client.BaseAddress = new("https://lrclib.net/api/")
            )
            .ConfigurePrimaryHttpMessageHandler(() => lrclibHandler);
        services
            .AddHttpClient(
                HttpClientNames.MusixMatch,
                client => client.BaseAddress = new("https://apic-desktop.musixmatch.com/ws/1.1/")
            )
            .ConfigurePrimaryHttpMessageHandler(() => musixmatchHandler);

        _serviceProvider = services.BuildServiceProvider();
        HttpClientProvider.Initialize(_serviceProvider.GetRequiredService<IHttpClientFactory>());
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => respond(request);
    }

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static HttpResponseMessage OkJson<T>(T body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonConvert.SerializeObject(body, JsonHelper.Settings),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static MusixMatchSubtitleGet SubtitleResponse(
        string title,
        string artist,
        int durationSeconds,
        double lineTimestamp
    ) =>
        new()
        {
            Message = new()
            {
                Body = new()
                {
                    MacroCalls = new()
                    {
                        TrackSubtitlesGet = new()
                        {
                            Message = new()
                            {
                                Body = new()
                                {
                                    SubtitleList =
                                    [
                                        new()
                                        {
                                            Subtitle = new()
                                            {
                                                SubtitleBody =
                                                [
                                                    new()
                                                    {
                                                        Text = "line one",
                                                        Time = new() { Total = lineTimestamp },
                                                    },
                                                ],
                                            },
                                        },
                                    ],
                                },
                            },
                        },
                        MatcherTrackGet = new()
                        {
                            Message = new()
                            {
                                Body = new()
                                {
                                    MusixMatchMusixMatchTrack = new()
                                    {
                                        TrackName = title,
                                        ArtistName = artist,
                                        TrackLength = durationSeconds,
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

    [Fact]
    public async Task SearchLyrics_LrclibGetSyncedHit_ShortCircuitsAndLogsWinner()
    {
        string title = Unique("Title");
        string artist = Unique("Artist");
        Track track = MakeTrack(title, artist, "Album", 300);

        int musixmatchCalls = 0;
        ConfigureHttp(
            new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/get"))
                    return Task.FromResult(
                        OkJson(
                            new LrclibSongResult
                            {
                                TrackName = title,
                                ArtistName = artist,
                                Duration = 300,
                                SyncedLyrics = "[00:01.00]line one",
                            }
                        )
                    );
                return Task.FromResult(NotFound());
            }),
            new ScriptedHandler(_ =>
            {
                Interlocked.Increment(ref musixmatchCalls);
                return Task.FromResult(NotFound());
            })
        );

        LyricsFetchResult result = await new LyricsAggregator().SearchLyrics(track);

        result.IsTransientError.Should().BeFalse();
        result.Lines.Should().NotBeNull();
        result.Winner.Should().Be("Lrclib-get");
        result.Source.Should().Be(LyricsSources.Lrclib);
        result.Synced.Should().BeTrue();
        musixmatchCalls
            .Should()
            .Be(0, "a synced /get hit is authoritative and must skip Musixmatch entirely");

        _logs
            .Should()
            .Contain(entry =>
                entry.Type == "lyrics"
                && entry.Message.Contains("via Lrclib-get")
                && entry.Message.Contains("synced=True")
            );
    }

    [Fact]
    public async Task SearchLyrics_RaceRejectsWrongSongEvenWhenItArrivesFirst()
    {
        string title = Unique("Title");
        string artist = Unique("Artist");
        Track track = MakeTrack(title, artist, "Album", 300);

        ConfigureHttp(
            new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/get"))
                    return Task.FromResult(NotFound());

                // /search answers immediately with a completely different song.
                // A race that just took "whichever finished first" would let
                // this win; PickBest must reject it on title/artist score.
                return Task.FromResult(
                    OkJson<LrclibSongResult[]>([
                        new()
                        {
                            TrackName = "Some Other Song Entirely",
                            ArtistName = "A Totally Different Band",
                            Duration = 300,
                            SyncedLyrics = "[00:01.00]wrong song",
                        },
                    ])
                );
            }),
            new ScriptedHandler(async _ =>
            {
                // Musixmatch is slower but has the real match. If arrival order
                // decided the winner, the fast wrong Lrclib result above would
                // already have won by the time this responds.
                await Task.Delay(TimeSpan.FromMilliseconds(75));
                return OkJson(SubtitleResponse(title, artist, 300, 1.0));
            })
        );

        LyricsFetchResult result = await new LyricsAggregator().SearchLyrics(track);

        result.IsTransientError.Should().BeFalse();
        result.Lines.Should().NotBeNull();
        result.Lines!.Single().Text.Should().Be("line one");
        result.Winner.Should().Be("Musixmatch-tight");
        result.Source.Should().Be(LyricsSources.Musixmatch);
        result.Synced.Should().BeTrue();
    }

    [Fact]
    public async Task SearchLyrics_ProviderTimeout_DoesNotHangAndIsTransient()
    {
        string title = Unique("Title");
        string artist = Unique("Artist");
        Track track = MakeTrack(title, artist, "Album", 300);

        ConfigureHttp(
            new ScriptedHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/get"))
                    return Task.FromResult(NotFound());
                return Task.FromResult(OkJson<LrclibSongResult[]>([]));
            }),
            // Musixmatch never answers. Real code has no per-provider
            // CancellationToken threaded into the HTTP call, so this keeps
            // "running" in the background for the life of the test process --
            // exactly the scenario the aggregator's WaitAsync bound exists for.
            new ScriptedHandler(async _ =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                return NotFound();
            })
        );

        LyricsAggregator aggregator = new(TimeSpan.FromMilliseconds(150));
        Stopwatch stopwatch = Stopwatch.StartNew();

        LyricsFetchResult result = await aggregator.SearchLyrics(track);

        stopwatch.Stop();
        // Generous bound: proves the 30s hang was abandoned, not a tight perf
        // assertion. The shared per-provider Queue rate limiter (1 req/s) may
        // add a little real queueing delay from other tests in this process.
        // The bound stays under the 30s hang and above the worst loaded-runner delay
        // seen on the shared self-hosted pool (14s).
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25));
        result.IsTransientError.Should().BeTrue();
        result.Lines.Should().BeNull();
    }
}
