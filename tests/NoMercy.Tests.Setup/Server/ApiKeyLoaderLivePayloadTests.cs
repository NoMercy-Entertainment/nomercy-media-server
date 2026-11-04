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
using NoMercy.NmSystem.Auth;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// Requirement: running the real <see cref="ApiKeyLoader"/> against the real response
/// envelope must leave every provider credential populated on the store the provider
/// clients actually read — <see cref="ApiKeyStore.Current"/>.
/// </summary>
/// <remarks>
/// The sibling tests build their payload by serializing the DTO, so they agree with
/// themselves whichever names the attributes carry, and they assert against the store
/// instance they injected rather than the static the clients resolve. Neither can see
/// the failure this exists for: a field that stays empty all the way to the request.
///
/// The JSON here is the envelope shape returned by a live <c>GET /v1/info</c>
/// (2026-08-02) with the secrets replaced — keys nested under <c>data.keys</c>, using
/// the names the API really sends. Whatever this test enters through, it reads out of
/// <c>ApiKeyStore.Current</c>, which is the same property
/// <c>AcoustIdFingerprintClient</c> reads when it builds <c>client=</c>.
///
/// This still does not prove a live AcoustID lookup returns 200 — that needs a running
/// server and is verified separately against the released binary. What it does prove is
/// that nothing between the HTTP response and the store drops the value.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class ApiKeyLoaderLivePayloadTests : IDisposable
{
    private readonly LocalStorageDriver _driver = new();
    private readonly ApiKeyStore _apiKeyStore = new();
    private readonly AuthTokenStore _authTokenStore = new();

    public void Dispose()
    {
        if (_driver.FileExists(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile))
            _driver.DeleteFile(NoMercy.NmSystem.Information.AppFiles.ApiKeysFile);
    }

    private const string LiveEnvelope = """
        {
            "status": "ok",
            "data": {
                "state": "Alpha",
                "version": "0.1.0",
                "quote": "test quote",
                "colors": ["#111111"],
                "keys": {
                    "makemkv_key": "makemkv-value",
                    "omdb_key": "omdb-value",
                    "tadb_key": "tadb-value",
                    "tmdb_key": "tmdb-value",
                    "tmdb_token": "tmdb-token-value",
                    "tvdb_key": "tvdb-value",
                    "fanart_key": "fanart-value",
                    "rotten_tomatoes": "rotten-value",
                    "acoustic_id": "acoustid-value",
                    "musixmatch_key": "musixmatch-value"
                }
            }
        }
        """;

    [Fact]
    public async Task LoadingTheLiveEnvelope_PopulatesEveryCredentialOnTheSharedStore()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, LiveEnvelope);
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        ApiKeyLoader loader = new(
            _authTokenStore,
            NullLogger<ApiKeyLoader>.Instance,
            _apiKeyStore,
            _driver
        );
        await loader.LoadKeys();

        Assert.True(_apiKeyStore.KeysLoaded);

        // Read through the static the provider clients resolve, not the injected
        // instance — a loader that fills a store nobody reads is the same outage.
        IApiKeyStore shared = ApiKeyStore.Current;

        Assert.Equal("acoustid-value", shared.AcousticIdKey);
        Assert.Equal("makemkv-value", shared.MakeMkvKey);
        Assert.Equal("tmdb-token-value", shared.TmdbToken);
        Assert.Equal("tmdb-value", shared.TmdbKey);
        Assert.Equal("omdb-value", shared.OmdbKey);
        Assert.Equal("tadb-value", shared.TadbKey);
        Assert.Equal("tvdb-value", shared.TvdbKey);
        Assert.Equal("fanart-value", shared.FanArtApiKey);
        Assert.Equal("rotten-value", shared.RottenTomatoes);
        Assert.Equal("musixmatch-value", shared.MusixmatchKey);
    }

    /// <summary>
    /// The empty credential is the whole failure: AcoustID answers a blank client with
    /// <c>400 missing required parameter "client"</c>, which reads in the log as a
    /// fingerprinting problem rather than a configuration one.
    /// </summary>
    [Fact]
    public async Task TheAcoustIdCredential_IsNeverLeftBlankByASuccessfulLoad()
    {
        using LoopbackHttpServer server = new();
        server.Handler = _ => new(200, LiveEnvelope);
        using ExternalServicesConfigScope scope = new(apiBaseUrl: server.BaseUrl);

        await new ApiKeyLoader(
            _authTokenStore,
            NullLogger<ApiKeyLoader>.Instance,
            _apiKeyStore,
            _driver
        ).LoadKeys();

        Assert.False(string.IsNullOrEmpty(ApiKeyStore.Current.AcousticIdKey));
    }
}
