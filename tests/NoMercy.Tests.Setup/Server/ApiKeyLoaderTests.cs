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

using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.NmSystem.Auth;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement: API keys must load from the live provider API first, fall back to the
/// on-disk cache when the network is unreachable, warn (but still apply) when that cache
/// is stale (&gt;30 days old), and only give up entirely — leaving provider features
/// unavailable — when neither source has anything. A response with an empty TMDB token
/// (an expired auth token masquerading as success) must be treated as if the network
/// call failed, not as valid data.
/// </summary>
/// <remarks>
/// <see cref="ApiKeyLoader"/> builds its own <c>GenericHttpClient</c> pointed at
/// <c>ExternalServicesConfig.Current.ApiBaseUrl</c> — a real loopback
/// <see cref="LoopbackHttpServer"/> exercises the actual HTTP contract. Cache file I/O
/// goes through a real <see cref="LocalStorageDriver"/> against the isolated
/// NOMERCY_APP_PATH temp tree the test harness already provides.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class ApiKeyLoaderTests : IDisposable
{
    private readonly LocalStorageDriver _driver = new();
    private readonly ApiKeyStore _apiKeyStore = new();
    private readonly AuthTokenStore _authTokenStore = new();

    // Every loop a test starts stops with the test, before its cache file is removed.
    private readonly CancellationTokenSource _stop = new();

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();

        if (_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile))
            _driver.DeleteFile(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile);
    }

    private ApiKeyLoader BuildLoader(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_authTokenStore, NullLogger<ApiKeyLoader>.Instance, _apiKeyStore, _driver, delay);

    private static string ApiInfoJson(string tmdbToken = "tmdb-token-1", string? cachedAt = null) =>
        JsonConvert.SerializeObject(
            new ApiInfoResponse
            {
                Status = "ok",
                CachedAt = cachedAt,
                Data = new()
                {
                    Keys = new() { TmdbToken = tmdbToken, TmdbKey = "tmdb-key-1" },
                    Quote = "test quote",
                    Colors = ["#111111"],
                },
            }
        );

    /// <summary>
    /// A real loopback server that always fails fast with a TERMINAL (non-5xx) status.
    /// GenericHttpClient's own resilience policy retries 5xx/408/429 responses AND
    /// connection-level exceptions (e.g. a closed port) up to 3 times with a 2s/4s/8s
    /// backoff — pointing at an actually-unreachable port would make every "network
    /// failure" test here take 14+ real seconds for no test value. A 400 is treated as
    /// terminal by that policy (no internal retry) while still exercising exactly the
    /// same ApiKeyLoader.TryFetchFromNetwork catch path as a real network failure.
    /// </summary>
    private static LoopbackHttpServer FastFailingServer()
    {
        LoopbackHttpServer server = new();
        server.Handler = _ => new(400, "bad request");
        return server;
    }

    [Fact]
    public async Task LoadKeys_NetworkSuccess_AppliesKeysAndWritesCache()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, ApiInfoJson("fresh-tmdb-token"));
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.True(_apiKeyStore.KeysLoaded);
        Assert.Equal("fresh-tmdb-token", _apiKeyStore.TmdbToken);
        Assert.True(_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile));
    }

    [Fact]
    public async Task LoadKeys_NetworkResponseEmptyTmdbToken_TreatedAsInvalid_NoCacheAvailable()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, ApiInfoJson(tmdbToken: string.Empty));
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_NetworkUnreachable_NoCacheAvailable_NoKeysApplied()
    {
        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_NetworkUnreachable_RecentCache_AppliesCachedKeys()
    {
        await SeedCacheFile(ApiInfoJson("cached-token", cachedAt: DateTime.UtcNow.ToString("O")));

        // The background refresh that fires after a cache hit must never block LoadKeys —
        // give it a no-op delay so any retry attempts fail fast against the same
        // unreachable endpoint instead of sleeping through the real 30s+ schedule.
        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader(delay: (_, _) => Task.CompletedTask);

        using CancellationTokenSource cts = new();
        await loader.LoadKeys(cts.Token);
        cts.Cancel();

        Assert.True(_apiKeyStore.KeysLoaded);
        Assert.Equal("cached-token", _apiKeyStore.TmdbToken);
    }

    [Fact]
    public async Task LoadKeys_NetworkUnreachable_StaleCache_StillAppliesCachedKeys()
    {
        await SeedCacheFile(
            ApiInfoJson("stale-cached-token", cachedAt: DateTime.UtcNow.AddDays(-45).ToString("O"))
        );

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader(delay: (_, _) => Task.CompletedTask);

        using CancellationTokenSource cts = new();
        await loader.LoadKeys(cts.Token);
        cts.Cancel();

        // A cache over 30 days old logs a warning instead of info, but a self-hosted
        // server with no network at all still needs SOMETHING to run providers with —
        // it must be applied, not discarded.
        Assert.True(_apiKeyStore.KeysLoaded);
        Assert.Equal("stale-cached-token", _apiKeyStore.TmdbToken);
    }

    [Fact]
    public async Task LoadKeys_CacheFileEmpty_TreatedAsNoCache()
    {
        await SeedCacheFile(string.Empty);

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_CacheFileMalformedJson_TreatedAsNoCache()
    {
        await SeedCacheFile("{not-valid-json-at-all");

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_CacheFileKeysExplicitlyNull_TreatedAsNoCache()
    {
        // "data":{"keys":null} explicitly overrides the property's non-null default —
        // unlike an absent "data"/"keys" key, which Newtonsoft leaves at the class's own
        // `= new()` default and is therefore NOT null (see the empty-token test below
        // for the requirement that actually applies to a plain `{"status":"ok"}` cache).
        await SeedCacheFile(
            JsonConvert.SerializeObject(new { status = "ok", data = new { keys = (object?)null } })
        );

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_CacheFileEmptyTmdbToken_TreatedAsNoCache()
    {
        // A cache entry with a keys section but a blank TMDB token is what a hand-edited
        // or corrupted cache file looks like (WriteCacheFile only ever persists a
        // response that already passed this same non-empty check) — must be discarded
        // exactly like the live-fetch path discards an empty-token API response,
        // otherwise KeysLoaded flips true with every provider key blank.
        await SeedCacheFile(ApiInfoJson(tmdbToken: string.Empty));

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);
        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.False(_apiKeyStore.KeysLoaded);
    }

    [Fact]
    public async Task LoadKeys_KeysAlreadyLoaded_SkipsNetworkEntirely()
    {
        // ServerBootstrapper and BootOrchestrator both call LoadKeys unconditionally on
        // the same IApiKeyStore singleton — without reading the flag ApplyKeys already
        // sets, the second call always re-fetched from the network for no reason.
        using LoopbackHttpServer server = new();
        int requestCount = 0;
        server.Handler = _ =>
        {
            Interlocked.Increment(ref requestCount);
            return new(200, ApiInfoJson("fresh-tmdb-token"));
        };
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);
        Assert.Equal(1, requestCount);

        await loader.LoadKeys(_stop.Token);

        Assert.Equal(1, requestCount);
        Assert.Equal("fresh-tmdb-token", _apiKeyStore.TmdbToken);
    }

    [Fact]
    public async Task LoadKeys_NoCacheDirectoryYet_DoesNotThrowWhenWritingCache()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, ApiInfoJson());
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        if (_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile))
            _driver.DeleteFile(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile);

        ApiKeyLoader loader = BuildLoader();
        await loader.LoadKeys(_stop.Token);

        Assert.True(_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile));
    }

    [Fact]
    public async Task LoadKeys_BackgroundRefresh_EventuallyAppliesFreshKeysOnceNetworkReturns()
    {
        await SeedCacheFile(ApiInfoJson("cached-token", cachedAt: DateTime.UtcNow.ToString("O")));

        int attempt = 0;
        using LoopbackHttpServer server = new();
        server.Handler = _ =>
        {
            int thisAttempt = Interlocked.Increment(ref attempt);
            // Fail the initial foreground fetch AND the first background retry, then
            // succeed — proves the retry loop actually loops, not just fires once.
            // 400 (not 5xx): avoids GenericHttpClient's own internal retry-on-transient
            // policy stacking its 2s/4s/8s backoff on top of this test's own retries.
            return thisAttempt <= 2
                ? new(400, "still down")
                : new(200, ApiInfoJson("refreshed-token"));
        };
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        // No-op delay: the retry loop's real backoff starts at 30 real seconds, which
        // would make this test impractically slow without the injectable delay seam.
        ApiKeyLoader loader = BuildLoader(delay: (_, _) => Task.CompletedTask);

        using CancellationTokenSource cts = new();
        await loader.LoadKeys(cts.Token);

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (_apiKeyStore.TmdbToken != "refreshed-token" && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        cts.Cancel();

        Assert.Equal("refreshed-token", _apiKeyStore.TmdbToken);
        Assert.True(attempt >= 3, $"expected at least 3 fetch attempts, saw {attempt}");
    }

    [Fact]
    public async Task LoadKeys_BackgroundRefresh_CancelledMidLoop_StopsCleanly()
    {
        await SeedCacheFile(ApiInfoJson("cached-token", cachedAt: DateTime.UtcNow.ToString("O")));

        using LoopbackHttpServer server = FastFailingServer();
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        // A long real delay so cancellation fires while the loop is awaiting it —
        // exercises the OperationCanceledException break branch specifically.
        ApiKeyLoader loader = BuildLoader(delay: (_, ct) => Task.Delay(Timeout.Infinite, ct));

        using CancellationTokenSource cts = new();
        await loader.LoadKeys(cts.Token);

        await Task.Delay(50);
        cts.Cancel();
        await Task.Delay(50);

        // No assertion beyond "did not throw / did not hang" — this locks the
        // cancellation-during-wait branch inside the background refresh loop.
    }

    [Fact]
    public async Task A_cancelled_background_refresh_never_fetches_or_writes_the_cache()
    {
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new();

        using (LoopbackHttpServer down = FastFailingServer())
        using (new ExternalServicesConfigScope(apiBaseUrl: down.BaseUrl))
        {
            // A wait that ends on its own just as the owner stops the loop.
            ApiKeyLoader loader = BuildLoader(
                delay: async (_, _) =>
                {
                    waiting.TrySetResult();
                    await release.Task;
                }
            );
            await loader.LoadKeys(cts.Token);
        }

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        // The address now belongs to someone else, as it does for the next test.
        int asked = 0;
        using LoopbackHttpServer later = new();
        later.Handler = _ =>
        {
            Interlocked.Increment(ref asked);
            return new(200, ApiInfoJson("someone-elses-token"));
        };
        using ExternalServicesConfigScope scope = new(apiBaseUrl: later.BaseUrl);

        release.TrySetResult();
        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.Equal(0, asked);
        Assert.False(_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile));
    }

    private async Task SeedCacheFile(string content)
    {
        string directory = System.IO.Path.GetDirectoryName(
            NoMercy.NmSystem.Information.AppFiles.ApiKeysFile
        )!;
        if (!_driver.DirectoryExists(directory))
            System.IO.Directory.CreateDirectory(directory);

        await using Stream stream = _driver.OpenWrite(
            NoMercy.NmSystem.Information.AppFiles.ApiKeysFile,
            overwrite: true
        );
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(content);
    }
}
