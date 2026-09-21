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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hub;
using NoMercy.Plugins.Library;
using NoMercy.Plugins.Network;
using NoMercy.Plugins.Quotas;
using NoMercy.Plugins.Runtime;
using NoMercy.Plugins.Storage;
using NoMercy.Storage;

namespace NoMercy.Plugins;

/// <summary>
/// Assembles a plugin's context, applying the trust decisions in one place.
/// </summary>
public class PluginContextFactory(
    IEventBus eventBus,
    IServiceProvider services,
    IStorage storage,
    IPluginGrantStore grantStore,
    IDataProtectionProvider protectionProvider,
    IPluginLibraryQuery libraryQuery,
    IPluginLibraryWriterFactory libraryWriterFactory,
    IPluginConfiguration platformConfiguration,
    IPluginHubContextFactory hubContextFactory,
    IPluginEncoder? encoder = null,
    IPluginJobs? jobs = null,
    IPluginMusicQuery? musicQuery = null,
    IPluginAudioToolsFactory? audioToolsFactory = null,
    IPluginDerivedAudio? derivedAudio = null,
    IPluginMusicAnalysisWriterFactory? analysisWriterFactory = null,
    Func<IPluginMediaFactory?>? mediaFactory = null,
    IPluginLibraryScanner? libraryScanner = null,
    string? pluginsRoot = null,
    IPluginFolderCatalog? folderCatalog = null,
    IPluginGrantedLocations? grantedLocations = null,
    IPluginFreeSpaceProbe? freeSpace = null,
    PluginQuotaMeter? quotas = null,
    Version? serverVersion = null
) : IPluginContextFactory
{
    public IPluginContext Create(
        Ulid pluginId,
        string dataFolderPath,
        ILogger logger,
        PluginCapabilities? capabilities,
        string? pluginName = null,
        Version? pluginVersion = null
    )
    {
        PluginSecretStore secrets = new(pluginId, protectionProvider, platformConfiguration);
        PluginGrants grants = new(pluginId, grantStore);

        // A writer only exists when the plugin asked for the capability AND the
        // owner granted at least one library. Declaring it is not holding it —
        // the manifest states an intention and the grant is the permission.
        IPluginLibraryWriter? writer = null;
        if (PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.LibraryWrite))
            writer = libraryWriterFactory.CreateFor(pluginId);

        // The same rule as the writer: declaring a capability is an intention,
        // and a host that mediates nothing hands back null rather than a call
        // that throws. A plugin can check for the facade instead of catching.
        IPluginEncoder? encoderFacade = null;
        IPluginJobs? jobsFacade = null;
        if (PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.Encoder))
        {
            encoderFacade = encoder;

            // Jobs travels with the encoder because they are one story: asking
            // for work and learning what became of it. A plugin that can start
            // an encode and cannot see it finish deletes files on a guess.
            jobsFacade = jobs;
        }

        // The three analysis facades: each gated on its own hook, the same
        // "declaring is not holding" rule as the writer and the storage/encoder
        // pair above.
        IPluginAudioTools? audioToolsFacade = null;
        if (PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.AudioTools))
            audioToolsFacade = audioToolsFactory?.CreateFor(pluginId);

        IPluginDerivedAudio? derivedAudioFacade = null;
        if (PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.DerivedAudio))
            derivedAudioFacade = derivedAudio;

        IPluginMusicAnalysisWriter? analysisWriter = null;
        if (
            PluginCapabilityGuard.DeclaresHook(
                capabilities,
                PluginHookCapability.MusicAnalysisWrite
            )
        )
            analysisWriter = analysisWriterFactory?.CreateFor(pluginId);

        return new PluginContext(
            pluginId,
            eventBus,
            services,
            logger,
            dataFolderPath,
            storage,
            secrets,
            libraryQuery,
            grants,
            writer,
            capabilities,
            () => grantStore.Granted(pluginId, PluginGrantKind.NetworkHost),
            pluginName,
            pluginVersion,
            hubContextFactory.For(pluginId),
            encoderFacade,
            jobsFacade,
            musicQuery,
            audioToolsFacade,
            derivedAudioFacade,
            analysisWriter,
            mediaFactory?.Invoke()?.CreateFor(pluginId),
            // Only when the plugin holds a writer: importing is a write, and
            // one without the other is a door with no lock on it.
            writer is null
            || libraryScanner is null
                ? null
                : new PluginLibraryImport(pluginId, writer, libraryScanner, logger),
            HostStorage(pluginId),
            ServerInfo(pluginId),
            Net(pluginId)
        );
    }

    /// <summary>
    /// Sockets, checked against the manifest and the owner's answer on every
    /// call. Null on a host that wired no broker, where the facade refuses by
    /// name rather than opening a socket nothing checked.
    /// <para>
    /// Resolved here rather than taken as a constructor parameter, for the same
    /// reason as the media factory: the broker asks what a plugin declared,
    /// that answer comes from the manager, and the manager is built with this
    /// factory. Asking for it at registration closes the ring and the process
    /// goes down before any test can report why.
    /// </para>
    /// </summary>
    private IPluginNet? Net(Ulid pluginId)
    {
        IPluginCapabilityBroker? broker = services.GetService<IPluginCapabilityBroker>();
        IPluginManifestSource? manifestSource = services.GetService<IPluginManifestSource>();
        IPluginResourceLedger? ledger = services.GetService<IPluginResourceLedger>();

        return broker is null || manifestSource is null || ledger is null
            ? null
            : new PluginNet(pluginId, broker, manifestSource, ledger);
    }

    /// <summary>
    /// The plugin's own folders, and the owner's folders it was granted. Null
    /// on a host that wired no folder catalogue, where the facade refuses by
    /// name rather than opening a path nothing checked.
    /// </summary>
    private IPluginStorage? HostStorage(Ulid pluginId) =>
        pluginsRoot is null || folderCatalog is null
            ? null
            : new PluginHostStorage(pluginId, pluginsRoot, folderCatalog, grantStore, quotas);

    /// <summary>
    /// Refreshed as the context is built rather than read live: a plugin reads
    /// GrantedPaths in a loop, and a property that queried would turn its loop
    /// into the server's slowest one.
    /// </summary>
    private IPluginServerInfo? ServerInfo(Ulid pluginId)
    {
        if (grantedLocations is null || freeSpace is null)
            return null;

        grantedLocations.RefreshAsync(pluginId).GetAwaiter().GetResult();

        return new PluginServerInfo(
            pluginId,
            serverVersion ?? new Version(0, 0),
            grantedLocations,
            freeSpace,
            grantStore
        );
    }
}

/// <summary>
/// Builds the media facade for one plugin. Separate because the proxy needs an
/// HTTP client bound to that plugin's allowlist, which only the host can build.
/// </summary>
public interface IPluginMediaFactory
{
    IPluginMedia CreateFor(Ulid pluginId);

    /// <summary>
    /// The fetching half, for the route that serves a ticket. Separate from
    /// the facade a plugin holds: fetching is the host acting on a ticket it
    /// minted, and nothing a plugin calls.
    /// </summary>
    IPluginMediaFetcher FetcherFor(Ulid pluginId);
}

/// <summary>What the media route calls once a ticket has been checked.</summary>
public interface IPluginMediaFetcher
{
    Task<HttpResponseMessage> FetchAsync(
        PluginProxyRequest request,
        string? range,
        CancellationToken ct
    );
}

/// <summary>
/// Builds the writer for one plugin, or null when the owner has granted it no
/// library to write to.
/// </summary>
public interface IPluginLibraryWriterFactory
{
    IPluginLibraryWriter? CreateFor(Ulid pluginId);
}
