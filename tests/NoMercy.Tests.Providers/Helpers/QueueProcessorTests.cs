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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using NoMercy.Providers.Helpers;
using ProviderQueue = NoMercy.Providers.Helpers.Queue;

namespace NoMercy.Tests.Providers.Helpers;

public class QueueProcessorTests
{
    [Fact]
    public void ProviderFamilies_ShareOneQueue()
    {
        ProviderQueues
            .For(HttpClientNames.TmdbImage)
            .Should()
            .BeSameAs(ProviderQueues.For(HttpClientNames.Tmdb));
        ProviderQueues
            .For(HttpClientNames.TvdbLogin)
            .Should()
            .BeSameAs(ProviderQueues.For(HttpClientNames.Tvdb));
        ProviderQueues
            .For(HttpClientNames.FanArtImage)
            .Should()
            .BeSameAs(ProviderQueues.For(HttpClientNames.FanArt));
        ProviderQueues
            .For(HttpClientNames.CoverArtImage)
            .Should()
            .BeSameAs(ProviderQueues.For(HttpClientNames.CoverArt));
    }

    [Fact]
    public async Task Queue_CapsConcurrentWork()
    {
        ProviderQueue queue = new(new() { Concurrent = 2, Interval = 1 });
        int active = 0;
        int peak = 0;
        Task<int>[] work = Enumerable
            .Range(0, 8)
            .Select(index =>
                queue.Enqueue(
                    async () =>
                    {
                        int count = Interlocked.Increment(ref active);
                        lock (queue)
                            peak = Math.Max(peak, count);
                        await Task.Delay(30);
                        Interlocked.Decrement(ref active);
                        return index;
                    },
                    "test"
                )
            )
            .ToArray();

        await Task.WhenAll(work);
        peak.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Queue_SpacesRequestStarts()
    {
        ProviderQueue queue = new(new() { Concurrent = 2, Interval = 100 });
        ConcurrentQueue<long> starts = new();
        Task<int>[] work = Enumerable
            .Range(0, 2)
            .Select(index =>
                queue.Enqueue(
                    async () =>
                    {
                        starts.Enqueue(Stopwatch.GetTimestamp());
                        await Task.Delay(10);
                        return index;
                    },
                    "test"
                )
            )
            .ToArray();

        await Task.WhenAll(work);
        long[] times = starts.ToArray();
        Stopwatch
            .GetElapsedTime(times[0], times[1])
            .Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(75));
    }

    [Fact]
    public async Task Queue_RespectsRetryAfterOn429()
    {
        ProviderQueue queue = new(new() { Concurrent = 1, Interval = 1 });
        int attempts = 0;
        Stopwatch watch = Stopwatch.StartNew();
        string result = await queue.Enqueue(
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    HttpRequestException failure = new(
                        "rate limited",
                        null,
                        HttpStatusCode.TooManyRequests
                    );
                    failure.Data["RetryAfter"] = TimeSpan.FromMilliseconds(50);
                    throw failure;
                }
                return Task.FromResult("ok");
            },
            "test"
        );

        result.Should().Be("ok");
        attempts.Should().Be(2);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(45));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Queue_UsesRetryAfterHeaderFromHttpResponse()
    {
        int attempts = 0;
        HttpClient client = new(
            new StubHandler(() =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(50));
                    return limited;
                }
                return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
            })
        )
        {
            BaseAddress = new("https://example.test/"),
        };
        ProviderQueue queue = new(new() { Concurrent = 1, Interval = 1 });
        Stopwatch watch = Stopwatch.StartNew();

        string result = await queue.Enqueue(
            () => ProviderHttp.GetStringAsync(client, "test"),
            "test"
        );

        result.Should().Be("ok");
        attempts.Should().Be(2);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(45));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Queue_RetriesHttp500()
    {
        ProviderQueue queue = new(
            new()
            {
                Concurrent = 1,
                Interval = 1,
                RetryBaseDelayMs = 50,
            }
        );
        int attempts = 0;
        Stopwatch watch = Stopwatch.StartNew();
        string result = await queue.Enqueue(
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new HttpRequestException(
                        "server error",
                        null,
                        HttpStatusCode.InternalServerError
                    );
                return Task.FromResult("ok");
            },
            "test"
        );

        result.Should().Be("ok");
        attempts.Should().Be(2);
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(45));
    }

    [Fact]
    public async Task Queue_ReturnsSuccessWithoutRetry()
    {
        ProviderQueue queue = new(new() { Concurrent = 1, Interval = 1 });
        int attempts = 0;
        string result = await queue.Enqueue(
            () =>
            {
                Interlocked.Increment(ref attempts);
                return Task.FromResult("ok");
            },
            "test"
        );

        result.Should().Be("ok");
        attempts.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Queue_ContinuesProcessing_AfterTransientError()
    {
        ProviderQueue queue = new(
            new()
            {
                Concurrent = 1,
                Interval = 10,
                Start = true,
            }
        );

        int callCount = 0;

        // First task throws
        try
        {
            await queue.Enqueue<string>(
                async () =>
                {
                    Interlocked.Increment(ref callCount);
                    await Task.Delay(1);
                    throw new InvalidOperationException("Transient failure");
                },
                "http://test/fail"
            );
        }
        catch (InvalidOperationException)
        {
            // Expected — Enqueue propagates via TaskCompletionSource
        }

        // Second task should still work (queue continues processing)
        string result = await queue.Enqueue<string>(
            async () =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(1);
                return "success";
            },
            "http://test/ok"
        );

        result.Should().Be("success");
        callCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Queue_PropagatesFailure_ToCaller()
    {
        ProviderQueue queue = new(
            new()
            {
                Concurrent = 1,
                Interval = 10,
                Start = true,
            }
        );

        InvalidOperationException thrownException = new("Test error for rejection");
        Exception? caught = null;

        // Failures surface to the caller via the per-task TaskCompletionSource
        // (the old Reject event was removed as dead code).
        try
        {
            await queue.Enqueue<string>(
                async () =>
                {
                    await Task.Delay(1);
                    throw thrownException;
                },
                "http://test/reject"
            );
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        caught!.Message.Should().Be("Test error for rejection");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Queue_ProcessesMultipleTasks_InOrder()
    {
        ProviderQueue queue = new(
            new()
            {
                Concurrent = 1,
                Interval = 10,
                Start = true,
            }
        );

        List<int> executionOrder = [];

        int result1 = await queue.Enqueue(
            async () =>
            {
                await Task.Delay(1);
                lock (executionOrder)
                    executionOrder.Add(1);
                return 1;
            },
            "http://test/1"
        );

        int result2 = await queue.Enqueue(
            async () =>
            {
                await Task.Delay(1);
                lock (executionOrder)
                    executionOrder.Add(2);
                return 2;
            },
            "http://test/2"
        );

        result1.Should().Be(1);
        result2.Should().Be(2);
        executionOrder.Should().ContainInOrder([1, 2]);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(send());
    }
}
