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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Ipc;

namespace NoMercy.PluginHost;

/// <summary>
/// The context the plugin holds, with every facade a proxy over the channel.
/// <para>
/// A plugin must not be able to tell that it moved out of the server process.
/// It holds the same <see cref="IPluginContext" /> the contract published, and
/// each facade turns its call into one named message instead of a method call
/// on an object in the same heap.
/// </para>
/// <para>
/// Three members stay local. The id and the data folder are fixed for the life
/// of the process, so a round trip for either would make every plugin pay for
/// the boundary on its first line. The logger writes to stdout, which the
/// supervisor drains into the server log — a log line is the one thing that
/// must still arrive when the channel is the thing that broke.
/// </para>
/// </summary>
public sealed class RemotePluginContext : IPluginContext
{
    private readonly PluginHostLaunch _launch;
    private readonly RemoteEvents _events;

    public RemotePluginContext(PluginHostLaunch launch, IPluginBrokerService broker)
    {
        _launch = launch;

        RemoteCall call = new(launch.PluginId, broker);

        _events = new RemoteEvents(call);
        Secrets = new RemoteSecrets(call);
        Grants = new RemoteGrants(call);
        Hub = new RemoteHub(call);
        Configuration = new RemoteConfiguration(call);
        Process = new RemoteProcess(launch.PluginId, call, launch, new LocalProcessStarter());
        Server = new RemoteServerInfo(call);
        Storage = new RemoteStorage(launch.PluginId, call);
        Net = new RemoteNet(launch.PluginId, call);
        Library = new RemoteLibrary(launch.PluginId, call);
        Metadata = new RemoteMetadata(call);
        Notifications = new RemoteNotifications(call);
        Users = new RemoteUsers(call);
        Scheduler = new RemoteScheduler(launch.PluginId, call);
        Settings = new RemoteSettings(launch.PluginId, call);
        User = new RemoteUserData(call);
        Call = call;
    }

    internal RemoteCall Call { get; }

    public Ulid PluginId => _launch.PluginId;

    public string DataFolderPath => _launch.DataFolder;

    public HttpClient HttpClient { get; } = new();

    public ILogger Logger { get; } =
        LoggerFactory.Create(builder => builder.AddSimpleConsole()).CreateLogger("plugin");

    public IPluginEvents Events => _events;

    public IPluginConfiguration Configuration { get; }

    public IPluginSecretStore Secrets { get; }

    public IPluginGrants Grants { get; }

    public IPluginHubContext Hub { get; }

    public IPluginProcess Process { get; }

    public IPluginServerInfo Server { get; }

    public IPluginStorage Storage { get; }

    public IPluginNet Net { get; }

    public IPluginLibraryQuery Library { get; }

    public IPluginMetadata Metadata { get; }

    public IPluginNotifications Notifications { get; }

    public IPluginUsers Users { get; }

    public IPluginScheduler Scheduler { get; }

    public IPluginSettings Settings { get; }

    public IPluginUserData User { get; }

    public IPluginLibraryWriter? LibraryWriter => null;

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default) =>
        _events.PublishAsync(name, payload, ct);

    /// <summary>Delivery of a subscribed topic, handed in by the host.</summary>
    public Task DeliverAsync(string topic, string payloadJson, CancellationToken ct = default) =>
        _events.DeliverAsync(topic, payloadJson, ct);

    private PluginRefusal NotYet(string facade) =>
        new(
            PluginRefusalCodes.HostServicesRemoved,
            _launch.PluginId.ToString(),
            $"A plugin in its own process asked for {facade}.",
            "This server runs the plugin out of process, and that facade does not cross the boundary yet.",
            "Run this plugin in the server's own process until the facade is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
            PluginRefusalSeverity.Blocked
        );
}
