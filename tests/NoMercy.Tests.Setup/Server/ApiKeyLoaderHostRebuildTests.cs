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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// #665: the server builds a new host after setup and after a port conflict. Each host's
/// loader stops its retry loop when the host's container is disposed, so a new host has
/// to load the keys itself when the first host could not (no bearer yet on a first boot,
/// or the API down with no cache), and has to keep refreshing keys the first host only
/// had from the cache; otherwise TMDB calls send an empty or outdated token until a
/// restart.
/// </summary>
/// <remarks>
/// Each host is a container built with the same <see cref="ApiKeyServiceCollectionExtensions.AddApiKeys"/>
/// the server uses, and "starting" it starts its hosted services the way the web host does.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class ApiKeyLoaderHostRebuildTests : IDisposable
{
    private readonly LocalStorageDriver _driver = new();
    private readonly LoopbackHttpServer _api = new();
    private readonly ExternalServicesConfigScope _config;

    public ApiKeyLoaderHostRebuildTests()
    {
        // A first boot has no key cache to fall back on, and no store a host before it
        // loaded (another test may have left one behind as the process store).
        DeleteKeyCache();
        _ = new ApiKeyStore();
        _config = new(apiBaseUrl: _api.BaseUrl);
    }

    public void Dispose()
    {
        _config.Dispose();
        _api.Dispose();
        DeleteKeyCache();
    }

    [Fact]
    public async Task A_rebuilt_host_loads_the_keys_the_first_host_could_not()
    {
        // The API answers a request without a bearer, but with no keys in it.
        _api.Handler = _ => new(200, ApiInfoJson(tmdbToken: string.Empty));

        using CancellationTokenSource firstHostShutdown = new();
        await using (ServiceProvider firstHost = BuildHost())
        {
            await firstHost.GetRequiredService<IApiKeyLoader>().LoadKeys(firstHostShutdown.Token);
            await StartAndSettleAsync(firstHost, firstHostShutdown.Token);

            Assert.False(firstHost.GetRequiredService<IApiKeyStore>().KeysLoaded);

            // Setup finished: the first host is shut down, and its retry loop with it.
            await firstHostShutdown.CancelAsync();
            await StopAsync(firstHost);
        }

        _api.Handler = _ => new(200, ApiInfoJson(tmdbToken: "tmdb-token-after-setup"));

        await using ServiceProvider rebuiltHost = BuildHost();
        await StartAndSettleAsync(rebuiltHost, CancellationToken.None);

        IApiKeyStore store = rebuiltHost.GetRequiredService<IApiKeyStore>();
        Assert.True(store.KeysLoaded);
        Assert.Equal("tmdb-token-after-setup", store.TmdbToken);
        Assert.Same(store, ApiKeyStore.Current);

        await StopAsync(rebuiltHost);
    }

    [Fact]
    public async Task A_rebuilt_host_keeps_the_loaded_keys_without_asking_the_api_again()
    {
        _api.Handler = _ => new(200, ApiInfoJson(tmdbToken: "tmdb-token-from-boot"));

        await using (ServiceProvider firstHost = BuildHost())
        {
            await firstHost.GetRequiredService<IApiKeyLoader>().LoadKeys();
            await StartAndSettleAsync(firstHost, CancellationToken.None);
            await StopAsync(firstHost);
        }

        Assert.Equal(1, _api.RequestCount);

        await using ServiceProvider rebuiltHost = BuildHost();
        await StartAndSettleAsync(rebuiltHost, CancellationToken.None);

        IApiKeyStore store = rebuiltHost.GetRequiredService<IApiKeyStore>();
        Assert.True(store.KeysLoaded);
        Assert.Equal("tmdb-token-from-boot", store.TmdbToken);
        Assert.Equal(1, _api.RequestCount);

        await StopAsync(rebuiltHost);
    }

    [Fact]
    public async Task A_rebuilt_host_refreshes_keys_the_first_host_only_had_from_the_cache()
    {
        await SeedKeyCache("cached-token");

        // No bearer yet: the API gives no keys, so the first host falls back to the cache.
        _api.Handler = _ => new(200, ApiInfoJson(tmdbToken: string.Empty));

        using CancellationTokenSource firstHostShutdown = new();
        await using (ServiceProvider firstHost = BuildHost(WaitUntilStopped))
        {
            await firstHost.GetRequiredService<IApiKeyLoader>().LoadKeys(firstHostShutdown.Token);
            await StartAndSettleAsync(firstHost, firstHostShutdown.Token);

            Assert.Equal("cached-token", firstHost.GetRequiredService<IApiKeyStore>().TmdbToken);

            await firstHostShutdown.CancelAsync();
            await StopAsync(firstHost);
        }

        // The keys were rotated since the cache was written.
        _api.Handler = _ => new(200, ApiInfoJson(tmdbToken: "rotated-token"));

        await using ServiceProvider rebuiltHost = BuildHost(NoWait);
        await StartAndSettleAsync(rebuiltHost, CancellationToken.None);

        IApiKeyStore store = rebuiltHost.GetRequiredService<IApiKeyStore>();
        await WaitUntilAsync(() => store.TmdbToken == "rotated-token");

        Assert.Equal("rotated-token", store.TmdbToken);
        Assert.Same(store, ApiKeyStore.Current);

        await StopAsync(rebuiltHost);
    }

    [Fact]
    public async Task Disposing_a_host_stops_its_retry_loop()
    {
        _api.Handler = _ => new(400, "down");
        TaskCompletionSource<CancellationToken> waiting = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        ServiceProvider host = BuildHost(
            (_, ct) =>
            {
                waiting.TrySetResult(ct);
                return Task.Delay(Timeout.Infinite, ct);
            }
        );

        // Degraded-mode recovery asks with no token at all, and the setup rebuild, the
        // HTTPS rebind and the port-conflict paths dispose a host without cancelling one.
        await host.GetRequiredService<IApiKeyLoader>().LoadKeys(CancellationToken.None);
        CancellationToken loopToken = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        int asked = _api.RequestCount;

        await host.DisposeAsync();

        Assert.True(loopToken.IsCancellationRequested);
        Assert.Equal(asked, _api.RequestCount);
    }

    private static Task WaitUntilStopped(TimeSpan _, CancellationToken ct) =>
        Task.Delay(Timeout.Infinite, ct);

    private static Task NoWait(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    /// <param name="retryDelay">
    /// Replaces the retry loop's real 30-second-and-up backoff; null keeps the server's own loader.
    /// </param>
    private ServiceProvider BuildHost(Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        ServiceCollection services = new();
        services.AddSingleton<IAuthTokenStore, AuthTokenStore>();
        services.AddSingleton<IStorageDriver>(_driver);
        services.AddSingleton<ILogger<ApiKeyLoader>>(NullLogger<ApiKeyLoader>.Instance);
        services.AddApiKeys();

        if (retryDelay is not null)
            services.AddSingleton<IApiKeyLoader>(provider => new ApiKeyLoader(
                provider.GetRequiredService<IAuthTokenStore>(),
                NullLogger<ApiKeyLoader>.Instance,
                provider.GetRequiredService<IApiKeyStore>(),
                _driver,
                retryDelay
            ));

        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    private async Task SeedKeyCache(string tmdbToken)
    {
        string directory = Path.GetDirectoryName(AppFiles.ApiKeysFile)!;
        if (!_driver.DirectoryExists(directory))
            _driver.CreateDirectory(directory);

        await using Stream stream = _driver.OpenWrite(AppFiles.ApiKeysFile, overwrite: true);
        await using StreamWriter writer = new(stream);
        await writer.WriteAsync(ApiInfoJson(tmdbToken, cachedAt: DateTime.UtcNow.ToString("O")));
    }

    /// <summary>
    /// Starts every hosted service and waits for the background ones to finish their
    /// work, so the assertions never race them.
    /// </summary>
    private static async Task StartAndSettleAsync(IServiceProvider host, CancellationToken ct)
    {
        IHostedService[] hostedServices = [.. host.GetServices<IHostedService>()];

        foreach (IHostedService hostedService in hostedServices)
            await hostedService.StartAsync(ct);

        foreach (IHostedService hostedService in hostedServices)
            if (hostedService is BackgroundService { ExecuteTask: { } running })
                await running.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task StopAsync(IServiceProvider host)
    {
        foreach (IHostedService hostedService in host.GetServices<IHostedService>())
            await hostedService.StopAsync(CancellationToken.None);
    }

    private void DeleteKeyCache()
    {
        if (_driver.FileExists(AppFiles.ApiKeysFile))
            _driver.DeleteFile(AppFiles.ApiKeysFile);
    }

    private static string ApiInfoJson(string tmdbToken, string? cachedAt = null) =>
        JsonConvert.SerializeObject(
            new ApiInfoResponse
            {
                Status = "ok",
                CachedAt = cachedAt,
                Data = new()
                {
                    Keys = new() { TmdbToken = tmdbToken, TmdbKey = "tmdb-key" },
                    Quote = "test quote",
                    Colors = ["#111111"],
                },
            }
        );
}
