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
    IPluginSecretStore secrets
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

        return request.Facade switch
        {
            "secrets" => await Secrets(request),
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
}
