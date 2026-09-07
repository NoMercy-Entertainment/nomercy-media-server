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
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Hub;
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
    IPluginStorage? pluginStorage = null,
    IPluginMusicQuery? musicQuery = null
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

        IPluginStorage? storageFacade = null;
        if (PluginCapabilityGuard.DeclaresHook(capabilities, PluginHookCapability.Storage))
            storageFacade = pluginStorage;

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
            storageFacade,
            musicQuery
        );
    }
}

/// <summary>
/// Builds the writer for one plugin, or null when the owner has granted it no
/// library to write to.
/// </summary>
public interface IPluginLibraryWriterFactory
{
    IPluginLibraryWriter? CreateFor(Ulid pluginId);
}
