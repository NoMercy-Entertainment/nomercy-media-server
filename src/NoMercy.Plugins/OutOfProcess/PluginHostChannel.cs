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
using Grpc.Net.Client;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc;
using ProtoBuf.Grpc.Client;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// The server's end of the call that goes the other way.
/// <para>
/// The broker channel carries what a plugin asks of the server. This carries
/// what the server asks of a plugin: initialize, invoke, health, shut down.
/// Both are needed, and only the first half had ever been built.
/// </para>
/// <para>
/// The token goes on every call for the same reason the broker checks one: a
/// local endpoint is reachable by anything else on the machine, and this one
/// drives the owner's plugin.
/// </para>
/// </summary>
public sealed class PluginHostChannel : IPluginHostService, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly IPluginHostService _host;
    private readonly Metadata _credentials;

    public PluginHostChannel(Ulid pluginId, string token)
    {
        string endpoint = PluginChannelEndpoints.HostFor(pluginId);

        _credentials = new Metadata { { PluginChannelEnvironment.TokenHeader, token } };

        _channel = GrpcChannel.ForAddress(
            PluginLocalTransport.AddressFor(endpoint),
            new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, ct) =>
                        await PluginLocalTransport.ConnectAsync(endpoint, ct),
                    EnableMultipleHttp2Connections = true,
                },
            }
        );

        _host = _channel.CreateGrpcService<IPluginHostService>();
    }

    public Task<PluginCallResponse> InitializeAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _host.InitializeAsync(request, Signed(context));

    public Task<PluginCallResponse> InvokeAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _host.InvokeAsync(request, Signed(context));

    public Task<PluginHealthSnapshot> HealthAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _host.HealthAsync(request, Signed(context));

    public Task<PluginCallResponse> ShutdownAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _host.ShutdownAsync(request, Signed(context));

    /// <summary>
    /// The token goes on the call the caller made, so the caller's own
    /// deadline and cancellation survive. A fresh context would drop both, and
    /// a shutdown that gave up would leave the process running.
    /// </summary>
    private CallContext Signed(CallContext context) =>
        new(
            new CallOptions(
                headers: _credentials,
                cancellationToken: context.CancellationToken,
                deadline: context.CallOptions.Deadline
            )
        );

    public void Dispose() => _channel.Dispose();
}
