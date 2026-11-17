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

using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Setup.Dto;
using NoMercy.Storage;

namespace NoMercy.Setup.Server;

/// <summary>
/// Loads the API keys of one host. A failed load, or keys that came only from the
/// on-disk cache, leave one retry loop running; it stops when the loader is disposed
/// with its host's container, so a discarded host never keeps asking the API.
/// </summary>
public sealed class ApiKeyLoader : IApiKeyLoader, IDisposable, IAsyncDisposable
{
    private readonly ILogger<ApiKeyLoader> _logger;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IStorageDriver _storageDriver;
    private static readonly int[] BackoffSeconds = [30, 60, 300, 900, 1800];

    private readonly IAuthTokenStore _authTokenStore;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _backgroundRefreshLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _backgroundRefresh;
    private bool _disposed;

    public ApiKeyLoader(
        IAuthTokenStore authTokenStore,
        ILogger<ApiKeyLoader> logger,
        IApiKeyStore apiKeyStore,
        IStorageDriver storageDriver
    )
        : this(authTokenStore, logger, apiKeyStore, storageDriver, delay: null) { }

    /// <summary>
    /// Internal (not exposed on the public constructor): lets NoMercy.Tests.Setup drive
    /// <see cref="StartBackgroundRefresh"/> through its retry backoff (30s-30m in
    /// production) without the real wall-clock wait — the only DI-visible constructor
    /// remains the public one above, so production callers and the built-in
    /// ServiceProvider are unaffected.
    /// </summary>
    internal ApiKeyLoader(
        IAuthTokenStore authTokenStore,
        ILogger<ApiKeyLoader> logger,
        IApiKeyStore apiKeyStore,
        IStorageDriver storageDriver,
        Func<TimeSpan, CancellationToken, Task>? delay
    )
    {
        _authTokenStore = authTokenStore;
        _logger = logger;
        _apiKeyStore = apiKeyStore;
        _storageDriver = storageDriver;
        _delay = delay ?? Task.Delay;
    }

    public async Task LoadKeys(CancellationToken ct = default)
    {
        // ServerBootstrapper and BootOrchestrator both call this unconditionally on the
        // same singleton — without reading the flag ApplyKeys already sets, the second
        // call always tried the network again for no reason.
        if (KeysFromNetwork)
            return;

        // Keys from the cache, possibly kept by a host rebuilt since (#665): they may be
        // outdated, so this loader keeps refreshing them. No fetch here, so a second
        // call at boot on an offline server does not wait for the network again.
        if (_apiKeyStore.KeysLoaded)
        {
            StartBackgroundRefresh(ct);
            return;
        }

        // 1. Try network first
        ApiInfoResponse? liveData = await TryFetchFromNetwork(ct);

        if (liveData is not null)
        {
            ApplyKeys(liveData, fromNetwork: true);
            await WriteCacheFile(liveData);
            _logger.LogInformation("API keys loaded from network");
            return;
        }

        // 2. Network failed — try cache
        ApiInfoResponse? cachedData = await TryReadCacheFile();

        if (cachedData is not null)
        {
            ApplyKeys(cachedData, fromNetwork: false);
            string cachedAt = cachedData.CachedAt ?? "unknown";

            DateTime? cachedAtDate = cachedData.CachedAt is not null
                ? DateTime.TryParse(
                    cachedData.CachedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTime parsed
                )
                    ? parsed
                    : null
                : null;

            if (cachedAtDate.HasValue && (DateTime.UtcNow - cachedAtDate.Value).TotalDays > 30)
            {
                _logger.LogWarning(
                    "API keys loaded from cache (cached at {CachedAt}) — cache is over 30 days old",
                    cachedAt
                );
            }
            else
            {
                _logger.LogInformation(
                    "API keys loaded from cache (cached at {CachedAt})",
                    cachedAt
                );
            }

            StartBackgroundRefresh(ct);
            return;
        }

        // 3. No network, no cache — provider features are unavailable for now, but a
        // first boot behind a flaky API (e.g. a Cloudflare 530 during setup) must
        // self-heal: keep retrying in the background exactly like the stale-cache
        // path instead of requiring a manual restart once the API is back.
        _logger.LogError(
            "API unreachable and no cached keys available — provider features will be unavailable until the API can be reached (retrying in background)"
        );
        StartBackgroundRefresh(ct);
    }

    /// <summary>
    /// Disposed with the host's container: stops the retry loop, also one started
    /// with no token or with a token nobody cancels.
    /// </summary>
    public void Dispose()
    {
        lock (_backgroundRefreshLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    /// <summary>As <see cref="Dispose"/>, and returns once the retry loop has ended.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();

        Task? backgroundRefresh;
        lock (_backgroundRefreshLock)
            backgroundRefresh = _backgroundRefresh;

        if (backgroundRefresh is null)
            return;

        // Disposing a container must not throw over a loop that already ended badly.
        try
        {
            await backgroundRefresh;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "API key refresh loop ended with an error");
        }
    }

    /// <summary>Keys fetched from the API in this process, not only read from the cache.</summary>
    private bool KeysFromNetwork =>
        _apiKeyStore is ApiKeyStore store ? store.KeysFromNetwork : _apiKeyStore.KeysLoaded;

    private async Task<ApiInfoResponse?> TryFetchFromNetwork(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Requesting server info");

            GenericHttpClient apiClient = new(ExternalServicesConfig.Current.ApiBaseUrl);
            apiClient.SetDefaultHeaders(
                ExternalServicesConfig.Current.UserAgent,
                _authTokenStore.AccessToken
            );

            string content = await apiClient.SendAndReadAsync(
                HttpMethod.Get,
                "v1/info",
                cancellationToken: ct
            );

            ApiInfoResponse? data = content.FromJson<ApiInfoResponse>();
            if (data?.Data.Keys is null)
                return null;

            if (string.IsNullOrEmpty(data.Data.Keys.TmdbToken))
            {
                _logger.LogWarning(
                    "API keys response contained empty keys — auth token may be expired, discarding response"
                );
                return null;
            }

            return data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch API keys from network");
            return null;
        }
    }

    private void ApplyKeys(ApiInfoResponse data, bool fromNetwork)
    {
        if (_apiKeyStore is ApiKeyStore store)
        {
            store.MakeMkvKey = data.Data.Keys.MakeMkvKey;
            store.TmdbKey = data.Data.Keys.TmdbKey;
            store.OmdbKey = data.Data.Keys.OmdbKey;
            store.FanArtApiKey = data.Data.Keys.FanArtKey;
            store.RottenTomatoes = data.Data.Keys.RottenTomatoes;
            store.AcousticIdKey = data.Data.Keys.AcousticIdKey;
            store.TadbKey = data.Data.Keys.TadbKey;
            store.TmdbToken = data.Data.Keys.TmdbToken;
            store.TvdbKey = data.Data.Keys.TvdbKey;
            store.MusixmatchKey = data.Data.Keys.MusixmatchKey;
            store.KeysLoaded = true;
            store.KeysFromNetwork = fromNetwork;
        }
    }

    private async Task WriteCacheFile(ApiInfoResponse data)
    {
        try
        {
            data.CachedAt = DateTime.UtcNow.ToString("O");
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            await using Stream stream = _storageDriver.OpenWrite(
                AppFiles.ApiKeysFile,
                overwrite: true
            );
            await using StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            await writer.WriteAsync(json);
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write API keys cache");
        }
    }

    private async Task<ApiInfoResponse?> TryReadCacheFile()
    {
        try
        {
            if (!_storageDriver.FileExists(AppFiles.ApiKeysFile))
                return null;

            string json;
            using (StreamReader reader = new(_storageDriver.OpenRead(AppFiles.ApiKeysFile)))
                json = await reader.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(json))
                return null;

            ApiInfoResponse? data = json.FromJson<ApiInfoResponse>();
            if (data?.Data.Keys is null)
                return null;

            // Mirrors TryFetchFromNetwork's own guard: a cache entry with no TMDB token
            // is not meaningfully different from "no cache" — accepting it as valid
            // would mark KeysLoaded true with every provider key blank. WriteCacheFile
            // only ever persists a response that already passed this exact check, so a
            // blank token here means the file was corrupted, hand-edited, or written by
            // an older/different build — treat it the same as a missing cache.
            if (string.IsNullOrEmpty(data.Data.Keys.TmdbToken))
            {
                _logger.LogWarning(
                    "Cached API keys file has an empty TMDB token — discarding as invalid"
                );
                return null;
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read API keys cache");
            return null;
        }
    }

    private void StartBackgroundRefresh(CancellationToken ct)
    {
        // Several callers ask this loader for keys (the bootstrapper, the boot
        // orchestrator, the host start, degraded-mode recovery). Each ask tries the API
        // once, but one retry loop per loader is enough; a second only doubles the calls.
        // The loop stops with the caller's token or with this loader, whichever is first:
        // degraded-mode recovery asks with no token, and some rebuild paths dispose a
        // container without cancelling its shutdown token.
        lock (_backgroundRefreshLock)
        {
            if (_disposed || _backgroundRefresh is { IsCompleted: false })
                return;

            _backgroundRefresh = RunBackgroundRefresh(
                CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct)
            );
        }
    }

    private Task RunBackgroundRefresh(CancellationTokenSource stop)
    {
        return Task.Run(async () =>
        {
            try
            {
                await RefreshUntilLoaded(stop.Token);
            }
            finally
            {
                stop.Dispose();
            }
        });
    }

    private async Task RefreshUntilLoaded(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            int delaySeconds = BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)];
            try
            {
                await _delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // A loop stopped during its wait or its fetch neither asks nor writes.
            if (ct.IsCancellationRequested)
                return;

            // Another loader on the same store (a later host's) already fetched them.
            if (KeysFromNetwork)
                return;

            ApiInfoResponse? fresh = await TryFetchFromNetwork(ct);
            if (ct.IsCancellationRequested)
                return;

            if (fresh is not null)
            {
                ApplyKeys(fresh, fromNetwork: true);
                await WriteCacheFile(fresh);
                _logger.LogInformation("API keys refreshed from network");
                return;
            }

            attempt++;
            _logger.LogWarning(
                "API key refresh attempt {Attempt} failed, retrying in {Delay}s",
                attempt,
                delaySeconds
            );
        }
    }
}
