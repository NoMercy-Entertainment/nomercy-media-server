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

using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Hub;
using NoMercy.Plugins.Network;
using NoMercy.Plugins.Player;
using NoMercy.Storage;

namespace NoMercy.Plugins;

public class PluginContext : IPluginContext
{
    public IEventBus EventBus { get; }
    public IServiceProvider Services { get; }
    public ILogger Logger { get; }
    public string DataFolderPath { get; }
    public IPluginConfiguration Configuration { get; }
    public HttpClient HttpClient { get; }
    public Ulid PluginId { get; }
    public IPluginSecretStore Secrets { get; }
    public IPluginLibraryQuery Library { get; }
    public IPluginMusicQuery? Music { get; }
    public IPluginLibraryWriter? LibraryWriter { get; }
    public IPluginGrants Grants { get; }
    public IPluginHubContext Hub { get; }

    /// <summary>
    /// Null when the plugin never declared the capability. Checking for it is
    /// the plugin's route, rather than calling and catching.
    /// </summary>
    public IPluginEncoder? Encoder { get; }

    public IPluginJobs? Jobs { get; }

    public IPluginStorage? Storage { get; }

    /// <summary>
    /// Playback, typed. Always present: the grants decide whether an intent
    /// reaches anyone, and a plugin branching on null for a surface that is part
    /// of its contract would be branching on how the host was wired rather than
    /// on what it is allowed to do.
    /// </summary>
    public IPluginPlayer Player { get; }

    public PluginContext(
        Ulid pluginId,
        IEventBus eventBus,
        IServiceProvider services,
        ILogger logger,
        string dataFolderPath,
        IStorage storage,
        IPluginSecretStore secrets,
        IPluginLibraryQuery library,
        IPluginGrants grants,
        IPluginLibraryWriter? libraryWriter = null,
        PluginCapabilities? capabilities = null,
        Func<IReadOnlyList<string>>? grantedHosts = null,
        string? pluginName = null,
        Version? pluginVersion = null,
        IPluginHubContext? hub = null,
        IPluginEncoder? encoder = null,
        IPluginJobs? jobs = null,
        IPluginStorage? pluginStorage = null,
        IPluginMusicQuery? music = null
    )
    {
        Encoder = encoder;
        Jobs = jobs;
        Storage = pluginStorage;

        PluginId = pluginId;
        EventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        DataFolderPath = dataFolderPath ?? throw new ArgumentNullException(nameof(dataFolderPath));
        Configuration = new PluginConfiguration(dataFolderPath, storage);
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Music = music;
        Grants = grants ?? throw new ArgumentNullException(nameof(grants));

        // Null when the plugin never declared the capability or the owner
        // granted no library. A plugin can check for it instead of calling and
        // catching.
        LibraryWriter = libraryWriter;

        // Never null: outside the web host there is no hub to map, and a plugin
        // calling Hub.PushAsync there should reach nobody rather than crash.
        Hub = hub ?? new NullPluginHubContext();

        Player = new PluginPlayer(pluginId, Hub, grants);

        HttpClient = PluginHttpClientFactory.Create(
            capabilities,
            grantedHosts,
            pluginId,
            pluginName,
            pluginVersion
        );
    }

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The envelope, not the plugin's own event class: a type declared in a
        // collectible load context has an identity no host subscriber can name,
        // so publishing one reaches nobody.
        return EventBus.PublishAsync(PluginMessageEvent.From(PluginId, name, payload), ct);
    }
}
