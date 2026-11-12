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

public class ApiKeyLoader : IApiKeyLoader
{
    private readonly ILogger<ApiKeyLoader> _logger;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly IStorageDriver _storageDriver;
    private static readonly int[] BackoffSeconds = [30, 60, 300, 900, 1800];

    private readonly IAuthTokenStore _authTokenStore;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

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
        if (_apiKeyStore.KeysLoaded)
            return;

        // 1. Try network first
        ApiInfoResponse? liveData = await TryFetchFromNetwork();

        if (liveData is not null)
        {
            ApplyKeys(liveData);
            await WriteCacheFile(liveData);
            _logger.LogInformation("API keys loaded from network");
            return;
        }

        // 2. Network failed — try cache
        ApiInfoResponse? cachedData = await TryReadCacheFile();

        if (cachedData is not null)
        {
            ApplyKeys(cachedData);
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

    private async Task<ApiInfoResponse?> TryFetchFromNetwork()
    {
        try
        {
            _logger.LogInformation("Requesting server info");

            GenericHttpClient apiClient = new(ExternalServicesConfig.Current.ApiBaseUrl);
            apiClient.SetDefaultHeaders(
                ExternalServicesConfig.Current.UserAgent,
                _authTokenStore.AccessToken
            );

            string content = await apiClient.SendAndReadAsync(HttpMethod.Get, "v1/info");

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch API keys from network");
            return null;
        }
    }

    private void ApplyKeys(ApiInfoResponse data)
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
        _ = Task.Run(
            async () =>
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

                    ApiInfoResponse? fresh = await TryFetchFromNetwork();
                    if (ct.IsCancellationRequested)
                        return;

                    if (fresh is not null)
                    {
                        ApplyKeys(fresh);
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
            },
            ct
        );
    }
}
