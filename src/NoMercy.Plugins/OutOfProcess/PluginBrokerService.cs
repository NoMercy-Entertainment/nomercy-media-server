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

using System.Text.Json;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Ipc;
using NoMercy.Plugins.Runtime;
using ProtoBuf.Grpc;

namespace NoMercy.Plugins.OutOfProcess;

/// <summary>
/// The server side of one plugin's channel, and the trust boundary of the
/// out-of-process runtime.
/// <para>
/// Nothing the child process sends is trusted. It names a plugin id, a facade
/// and a member, and each of those is a claim: the id is checked against the
/// plugin this broker was built for, the facade against the set the server
/// actually serves, and the capability before the facade is touched at all.
/// A facade that ran and then refused has already done the thing the refusal
/// was for.
/// </para>
/// <para>
/// One broker per plugin rather than one shared broker with the id as an
/// argument. A shared one would have to trust the id on every call, and a
/// compromised child could borrow another plugin's capabilities by typing its
/// id.
/// </para>
/// </summary>
public sealed class PluginBrokerService(
    Ulid pluginId,
    IPluginCapabilityBroker capabilities,
    IPluginSecretStore secrets,
    IPluginApprovedBinaries approved,
    IPluginServerInfo server,
    IPluginStorageRoots storage,
    IPluginLibraryQuery library
) : IPluginBrokerService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        if (request.PluginId != pluginId.ToString())
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"A plugin process called {request.Facade}.{request.Member} as another plugin.",
                "The id on the call is not the plugin this channel belongs to.",
                "This is a server fault rather than a plugin one. Report it with the server log around the call."
            );

        try
        {
            return await Route(request);
        }
        catch (PluginRefusedException refused)
        {
            // A facade that refused after the broker let the call through
            // still has to reach the plugin as a refusal. Thrown across the
            // channel it would arrive as a server that stopped answering.
            return PluginCallResponse.Refused(Wire(refused.Refusal));
        }
    }

    private async Task<PluginCallResponse> Route(PluginCallRequest request)
    {
        return request.Facade switch
        {
            "secrets" => await Secrets(request),
            "process" => Process(request),
            "server" => await Server(request),
            "storage" => await Storage(request),
            "net" => Net(request),
            "library" => await Library(request),
            _ => Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for {request.Facade}.{request.Member}.",
                $"This server does not serve a facade called {request.Facade} across the process boundary.",
                "Use a facade the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            ),
        };
    }

    private async Task<PluginCallResponse> Secrets(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, "secrets") is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        SecretCall call =
            JsonSerializer.Deserialize<SecretCall>(request.PayloadJson, Json) ?? new(null, null);

        switch (request.Member)
        {
            case nameof(IPluginSecretStore.GetAsync):
                return Value(await secrets.GetAsync(call.Key ?? string.Empty));

            case nameof(IPluginSecretStore.SetAsync):
                await secrets.SetAsync(call.Key ?? string.Empty, call.Value ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.DeleteAsync):
                await secrets.DeleteAsync(call.Key ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.KeysAsync):
                return Value(await secrets.KeysAsync());

            case nameof(IPluginSecretStore.GetForUserAsync):
                return Value(await secrets.GetForUserAsync(call.Key ?? string.Empty));

            case nameof(IPluginSecretStore.SetForUserAsync):
                await secrets.SetForUserAsync(call.Key ?? string.Empty, call.Value ?? string.Empty);
                return PluginCallResponse.Value("null");

            case nameof(IPluginSecretStore.DeleteForUserAsync):
                await secrets.DeleteForUserAsync(call.Key ?? string.Empty);
                return PluginCallResponse.Value("null");

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for secrets.{request.Member}.",
                    "The secrets facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    /// <summary>
    /// Answers which file, and starts nothing.
    /// <para>
    /// A process the server started would be a child of the server, outside
    /// the sandbox that holds the plugin. The plugin's own process starts it,
    /// so the child inherits the job object, the cgroup or the sandbox profile
    /// that already holds its parent.
    /// </para>
    /// </summary>
    private PluginCallResponse Process(PluginCallRequest request)
    {
        if (request.Member != nameof(Abstractions.IPluginProcess.SpawnAsync))
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for process.{request.Member}.",
                "The process facade has no member by that name.",
                "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            );

        SpawnCall call =
            JsonSerializer.Deserialize<SpawnCall>(request.PayloadJson, Json) ?? new(null);

        string binary = call.Binary ?? string.Empty;

        // The capability says whether this plugin may start anything, with the
        // binary as the scope the owner consented to.
        if (capabilities.Check(pluginId, PluginCapabilityNames.ProcessSpawn, binary) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        // The name is only a label; this is the file. A name the owner never
        // approved resolves to nothing, and nothing is what runs.
        string? path = approved.PathFor(pluginId, binary);

        if (path is null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.ProcessSpawnUndeclared(pluginId.ToString(), binary))
            );

        return Value(new PluginSpawnPermit(path));
    }

    /// <summary>
    /// What this server is, so a plugin branches on a fact.
    /// <para>
    /// No capability gate here: the version and the platform are facts about
    /// software the owner installed, and the granted paths are filtered by the
    /// grants already. The free-space probe refuses a folder the owner never
    /// granted on its own, so the refusal keeps the sentence it always had.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Server(PluginCallRequest request)
    {
        switch (request.Member)
        {
            case nameof(IPluginServerInfo.Version):
                return Value(server.Version.ToString());

            case nameof(IPluginServerInfo.Platform):
                return Value(server.Platform);

            case nameof(IPluginServerInfo.GrantedPaths):
                return Value(server.GrantedPaths);

            case nameof(IPluginServerInfo.FreeSpaceBytesAsync):
                FolderCall call =
                    JsonSerializer.Deserialize<FolderCall>(request.PayloadJson, Json) ?? new(null);

                return Value(await server.FreeSpaceBytesAsync(call.FolderId ?? string.Empty));

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for server.{request.Member}.",
                    "The server facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    /// <summary>
    /// Which folder, never the bytes in it.
    /// <para>
    /// The plugin's own three folders need no grant: the server made them for
    /// this plugin and nothing else can reach them. One of the owner's folders
    /// needs both the capability and a grant naming that folder, so the scope
    /// is the folder id and the check runs before the path is looked up.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Storage(PluginCallRequest request)
    {
        switch (request.Member)
        {
            case nameof(IPluginStorage.Private):
                return Value(storage.PrivateRoot);

            case nameof(IPluginStorage.Temp):
                return Value(storage.TempRoot);

            case nameof(IPluginStorage.Derived):
                return Value(storage.DerivedRoot);

            case nameof(IPluginStorage.PathAsync):
                return await OwnerFolder(request);

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for storage.{request.Member}.",
                    "The storage facade has no member by that name.",
                    "Use a member the contract declares. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    private async Task<PluginCallResponse> OwnerFolder(PluginCallRequest request)
    {
        FolderCall call =
            JsonSerializer.Deserialize<FolderCall>(request.PayloadJson, Json) ?? new(null);

        string folderId = call.FolderId ?? string.Empty;

        if (
            capabilities.Check(pluginId, PluginCapabilityNames.StoragePath, folderId) is { } refusal
        )
            return PluginCallResponse.Refused(Wire(refusal));

        string? path = await storage.PathForAsync(folderId);

        if (path is null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.FileOutsideGrant(pluginId.ToString(), folderId))
            );

        return Value(path);
    }

    /// <summary>
    /// Whether this plugin may reach that host, and nothing more.
    /// <para>
    /// The socket is opened in the plugin's own process. A socket proxied
    /// through the server would copy every byte of a download twice and put
    /// the server in the middle of a connection it has no reason to read.
    /// </para>
    /// <para>
    /// Asked twice, because the two answers have different fixes. Without the
    /// scope the question is only whether this plugin may dial at all; with
    /// it, whether it may dial there.
    /// </para>
    /// </summary>
    private PluginCallResponse Net(PluginCallRequest request)
    {
        if (request.Member != nameof(IPluginNet.DialAsync))
            return Refuse(
                PluginRefusalCodes.HostServicesRemoved,
                $"The plugin asked the server for net.{request.Member}.",
                "The net facade does not carry that member across the process boundary yet.",
                "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
            );

        DialCall call =
            JsonSerializer.Deserialize<DialCall>(request.PayloadJson, Json) ?? new(null, 0);

        string host = call.Host ?? string.Empty;

        if (capabilities.Check(pluginId, PluginCapabilityNames.NetworkDial) is not null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.SocketUndeclared(pluginId.ToString(), host, call.Port))
            );

        if (capabilities.Check(pluginId, PluginCapabilityNames.NetworkDial, host) is not null)
            return PluginCallResponse.Refused(
                Wire(PluginRefusalMessages.HostNotAllowed(pluginId.ToString(), host))
            );

        return PluginCallResponse.Value("true");
    }

    /// <summary>
    /// Reads of the owner's library, which is the one facade whose answer is
    /// the data itself rather than a permit.
    /// <para>
    /// A title and an episode count are small and the plugin holds no database
    /// handle, so there is nothing to hand it a path to. The rows cross, and
    /// the capability is checked before a single one is read: a query that ran
    /// and then refused has already told the server's disk what to look for.
    /// </para>
    /// </summary>
    private async Task<PluginCallResponse> Library(PluginCallRequest request)
    {
        if (capabilities.Check(pluginId, PluginCapabilityNames.LibraryRead) is { } refusal)
            return PluginCallResponse.Refused(Wire(refusal));

        LibraryCall call =
            JsonSerializer.Deserialize<LibraryCall>(request.PayloadJson, Json) ?? new(null, 0);

        switch (request.Member)
        {
            case nameof(IPluginLibraryQuery.GetLibrariesAsync):
                return Value(await library.GetLibrariesAsync());

            case nameof(IPluginLibraryQuery.GetShowsAsync):
                return Value(await library.GetShowsAsync(call.LibraryId));

            case nameof(IPluginLibraryQuery.GetMoviesAsync):
                return Value(await library.GetMoviesAsync(call.LibraryId));

            case nameof(IPluginLibraryQuery.GetEpisodesAsync):
                return Value(await library.GetEpisodesAsync(call.ShowId));

            case nameof(IPluginLibraryQuery.GetShowFilesAsync):
                return Value(await library.GetShowFilesAsync(call.ShowId));

            default:
                return Refuse(
                    PluginRefusalCodes.HostServicesRemoved,
                    $"The plugin asked the server for library.{request.Member}.",
                    "The library facade does not carry that member across the process boundary yet.",
                    "Run this plugin in the server's own process until it is carried across. Docs: /nomercy-plugins/handbook/runtime-and-isolation"
                );
        }
    }

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => CallAsync(request, context);

    /// <summary>
    /// Deliveries to the plugin. Empty until the hooks cross in their own
    /// task; an enumerable that ended would look to the child like a server
    /// that had nothing more to say rather than one that never started.
    /// </summary>
    public async IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        await Task.CompletedTask;
        yield break;
    }

    private static PluginCallResponse Value<T>(T value) =>
        PluginCallResponse.Value(JsonSerializer.Serialize(value, Json));

    private PluginCallResponse Refuse(string code, string what, string why, string fix) =>
        PluginCallResponse.Refused(
            new WireRefusal(
                code,
                pluginId.ToString(),
                what,
                why,
                fix,
                PluginRefusalSeverity.Blocked.ToString()
            )
        );

    private static WireRefusal Wire(PluginRefusal refusal) =>
        new(
            refusal.Code,
            refusal.Plugin,
            refusal.What,
            refusal.Why,
            refusal.Fix,
            refusal.Severity.ToString()
        );

    private sealed record SecretCall(string? Key, string? Value);

    private sealed record SpawnCall(string? Binary);

    private sealed record FolderCall(string? FolderId);

    private sealed record DialCall(string? Host, int Port);

    private sealed record LibraryCall(string? LibraryId, int ShowId);
}
