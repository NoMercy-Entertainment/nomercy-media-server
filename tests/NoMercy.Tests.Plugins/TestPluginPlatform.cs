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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Hub;
using NoMercy.Storage;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Builds the plugin platform's collaborators for a test, so a test that cares
/// about one of them does not have to name the other six.
/// </summary>
public static class TestPluginPlatform
{
    /// <summary>A grant store backed by an in-memory configuration.</summary>
    public static IPluginGrantStore GrantStore() =>
        new ConfigPluginGrantStore(new InMemoryPluginConfiguration());

    /// <summary>A secret store with an ephemeral key ring, for tests that need one to exist.</summary>
    public static IPluginSecretStore Secrets(Ulid pluginId = default) =>
        new PluginSecretStore(
            pluginId,
            new EphemeralDataProtectionProvider(),
            new InMemoryPluginConfiguration()
        );

    /// <summary>Grants for one plugin over a fresh store.</summary>
    public static IPluginGrants Grants(Ulid pluginId = default, IPluginGrantStore? store = null) =>
        new PluginGrants(pluginId, store ?? GrantStore());

    public static PluginContext Context(
        IEventBus eventBus,
        string dataFolder,
        IStorage storage,
        Ulid? pluginId = null,
        IServiceProvider? services = null,
        IPluginGrantStore? grants = null,
        IPluginLibraryQuery? library = null,
        IPluginLibraryWriter? writer = null,
        PluginCapabilities? capabilities = null
    )
    {
        Ulid id = pluginId ?? Ulid.Empty;
        IPluginGrantStore grantStore = grants ?? GrantStore();

        return new(
            id,
            eventBus,
            services ?? new EmptyServiceProvider(),
            NullLogger.Instance,
            dataFolder,
            storage,
            new PluginSecretStore(
                id,
                new EphemeralDataProtectionProvider(),
                new InMemoryPluginConfiguration()
            ),
            library ?? new NullPluginLibraryQuery(),
            new PluginGrants(id, grantStore),
            writer,
            capabilities,
            () => grantStore.Granted(id, PluginGrantKind.NetworkHost)
        );
    }

    public static IPluginContextFactory ContextFactory(
        IEventBus eventBus,
        IStorage storage,
        IPluginGrantStore? grants = null,
        IPluginLibraryQuery? library = null,
        IPluginLibraryWriterFactory? writerFactory = null
    ) =>
        new PluginContextFactory(
            eventBus,
            new EmptyServiceProvider(),
            storage,
            grants ?? GrantStore(),
            new EphemeralDataProtectionProvider(),
            library ?? new NullPluginLibraryQuery(),
            writerFactory ?? new NullPluginLibraryWriterFactory(),
            new InMemoryPluginConfiguration(),
            new NullPluginHubContextFactory()
        );

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// Configuration held in memory, so a store test exercises the real store
/// rather than the filesystem.
/// </summary>
public sealed class InMemoryPluginConfiguration : IPluginConfiguration
{
    private object? _value;

    public T? GetConfiguration<T>()
        where T : class, new() => _value as T;

    public Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new() => Task.FromResult(_value as T);

    public void SaveConfiguration<T>(T configuration)
        where T : class => _value = configuration;

    public Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class
    {
        _value = configuration;
        return Task.CompletedTask;
    }

    public bool HasConfiguration() => _value is not null;

    public void DeleteConfiguration() => _value = null;
}
