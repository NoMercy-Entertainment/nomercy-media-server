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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.Runtime;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// One broker per plugin, built from the same facades the in-process context
/// is built from.
/// <para>
/// Reusing <see cref="IPluginContext" /> rather than assembling the facades a
/// second time: a plugin must reach exactly the same things whichever side of
/// the boundary it is on, and two assembly sites are two answers that drift.
/// </para>
/// </summary>
public sealed class PluginBrokerFactory(
    IPluginContextFactory contexts,
    IPluginCapabilityBroker capabilities,
    IPluginApprovedBinaries approved,
    IPluginBundleSignature signature,
    IPluginStorageRootsFactory storageRoots,
    string pluginsPath
) : IPluginBrokerFactory
{
    public IPluginBrokerService For(Ulid pluginId)
    {
        string dataFolder = Path.Combine(pluginsPath, "data", pluginId.ToString());

        IPluginContext context = contexts.Create(
            pluginId,
            dataFolder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            capabilities: null
        );

        return new PluginBrokerService(
            pluginId,
            capabilities,
            context.Secrets,
            approved,
            context.Server,
            storageRoots.For(pluginId, dataFolder),
            context.Library,
            context.Metadata,
            context.Notifications,
            context.Users,
            context.Scheduler,
            context.Settings,
            context.User,
            context.LibraryWriter,
            Facade(() => context.LibraryImport),
            signature,
            Path.Combine(pluginsPath, pluginId.ToString()),
            Facade(() => context.Media)
        );
    }

    /// <summary>
    /// A facade the contract refuses rather than returns null for.
    /// <para>
    /// Reading one on a host that does not carry it throws, and the broker
    /// wants an absence it can answer with. Catching here turns the throw into
    /// the null the broker already knows how to report.
    /// </para>
    /// </summary>
    private static T? Facade<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (PluginRefusedException)
        {
            return null;
        }
    }
}

/// <summary>Builds one plugin's storage roots, which need its data folder.</summary>
public interface IPluginStorageRootsFactory
{
    IPluginStorageRoots For(Ulid pluginId, string dataFolder);
}
