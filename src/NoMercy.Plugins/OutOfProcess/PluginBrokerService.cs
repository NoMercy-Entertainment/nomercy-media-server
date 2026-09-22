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
    IPluginServerInfo server
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
}
