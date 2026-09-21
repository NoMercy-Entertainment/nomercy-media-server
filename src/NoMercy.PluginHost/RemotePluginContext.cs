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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>
/// The context the plugin holds, with every facade still on the far side.
/// <para>
/// A stub in this task: the process boots, loads the assembly and answers,
/// and everything a plugin asks the server for refuses by naming the facade.
/// Each facade becomes a proxy over the broker channel in its own task, so a
/// half-connected one never looks like a working one.
/// </para>
/// </summary>
public sealed class RemotePluginContext(PluginHostLaunch launch) : IPluginContext
{
    public Ulid PluginId => launch.PluginId;

    public string DataFolderPath => launch.DataFolder;

    public HttpClient HttpClient { get; } = new();

    public ILogger Logger { get; } =
        LoggerFactory.Create(builder => builder.AddSimpleConsole()).CreateLogger("plugin");

    public IPluginEvents Events =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Events)));

    public IPluginConfiguration Configuration =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Configuration)));

    public IPluginSecretStore Secrets =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Secrets)));

    public IPluginLibraryQuery Library =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Library)));

    public IPluginGrants Grants =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Grants)));

    public IPluginLibraryWriter? LibraryWriter => null;

    public IPluginHubContext Hub =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.Hub)));

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default) =>
        throw new PluginRefusedException(NotYet(nameof(IPluginContext.PublishAsync)));

    private static PluginRefusal NotYet(string facade) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            "unknown plugin",
            $"A plugin in its own process asked for {facade}.",
            "This server runs the plugin out of process, and that facade does not cross the boundary yet.",
            "Run this plugin in the server's own process until the facade is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
