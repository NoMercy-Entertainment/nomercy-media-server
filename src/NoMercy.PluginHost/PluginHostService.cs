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

using Grpc.Core;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc;

namespace NoMercy.PluginHost;

/// <summary>
/// The only way into this process, and every call carries the launch token.
/// <para>
/// The endpoint is local, which is not the same as private: anything running
/// as this user can open the pipe. The token is what makes it the server's
/// pipe, and it is checked before the plugin is touched rather than after.
/// </para>
/// </summary>
public sealed class PluginHostService(IHostedPlugin plugin, string token) : IPluginHostService
{
    public Task<PluginCallResponse> InitializeAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => Authorized(context) ? Task.FromResult(PluginCallResponse.Value("{}")) : Refuse();

    public async Task<PluginCallResponse> InvokeAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        if (!Authorized(context))
            return await Refuse();

        string result = await plugin.InvokeAsync(
            request.Member,
            request.PayloadJson,
            context.CancellationToken
        );

        return PluginCallResponse.Value(result);
    }

    public Task<PluginHealthSnapshot> HealthAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => Task.FromResult(PluginHostHealth.Read());

    public Task<PluginCallResponse> ShutdownAsync(
        PluginCallRequest request,
        CallContext context = default
    )
    {
        if (!Authorized(context))
            return Refuse();

        PluginHostLifetime.RequestStop();

        return Task.FromResult(PluginCallResponse.Value("{}"));
    }

    /// <summary>The call a test makes, carrying the token a server would send.</summary>
    public static CallContext ContextWithToken(string value) =>
        new(new CallOptions(new Metadata { { PluginChannelEnvironment.TokenHeader, value } }));

    private bool Authorized(CallContext context) =>
        context.RequestHeaders?.GetValue(PluginChannelEnvironment.TokenHeader) == token;

    private static Task<PluginCallResponse> Refuse() =>
        Task.FromResult(
            PluginCallResponse.Refused(
                new(
                    "PLUGIN_HOST_UNAVAILABLE",
                    "unknown plugin",
                    "A call reached the plugin process without the server's launch token.",
                    "Only the server that started this process may talk to it, and the token did not match.",
                    "Restart the plugin from its health page so the server mints a new token. Docs: /nomercy-plugins/handbook/runtime-and-isolation",
                    "blocked"
                )
            )
        );
}
