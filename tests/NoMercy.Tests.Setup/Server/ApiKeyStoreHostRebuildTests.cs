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
using NoMercy.Setup.Server;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup.Server;

// #663: the HTTPS host built after setup has its own container; its store must keep
// the keys the setup host loaded, or the first scan sends an empty TMDB token (401).
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class ApiKeyStoreHostRebuildTests
{
    private static IServiceProvider BuildHost()
    {
        ServiceCollection services = new();
        services.AddSingleton<IApiKeyStore>(_ => ApiKeyStore.ForHost());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void RebuiltHost_KeepsTheKeysTheFirstHostLoaded()
    {
        IServiceProvider setupHost = BuildHost();
        ApiKeyStore setupStore = (ApiKeyStore)setupHost.GetRequiredService<IApiKeyStore>();
        setupStore.TmdbToken = "tmdb-token-from-setup";
        setupStore.KeysLoaded = true;

        IServiceProvider httpsHost = BuildHost();
        IApiKeyStore httpsStore = httpsHost.GetRequiredService<IApiKeyStore>();

        Assert.True(httpsStore.KeysLoaded);
        Assert.Equal("tmdb-token-from-setup", httpsStore.TmdbToken);
        Assert.Equal("tmdb-token-from-setup", ApiKeyStore.Current.TmdbToken);
    }

    [Fact]
    public void FirstHost_WithNoLoadedKeys_GetsAFreshStore()
    {
        _ = new ApiKeyStore();

        IServiceProvider host = BuildHost();
        IApiKeyStore store = host.GetRequiredService<IApiKeyStore>();

        Assert.False(store.KeysLoaded);
        Assert.Same(store, ApiKeyStore.Current);
    }
}
