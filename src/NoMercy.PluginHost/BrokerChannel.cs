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

namespace NoMercy.PluginHost;

/// <summary>
/// The plugin process's end of the channel.
/// <para>
/// Every facade on <see cref="RemotePluginContext" /> ends here, so this is the
/// only place the plugin's process talks to the server at all. It carries the
/// launch token on every call: the endpoint is local, but local is not the same
/// as ours, and a second plugin on the same machine must not be able to call as
/// this one.
/// </para>
/// </summary>
public sealed class BrokerChannel : IPluginBrokerService, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly IPluginBrokerService _broker;
    private readonly Metadata _credentials;

    public BrokerChannel(PluginHostLaunch launch)
    {
        _credentials = new Metadata { { PluginChannelEnvironment.TokenHeader, launch.Token } };

        _channel = GrpcChannel.ForAddress(
            PluginLocalTransport.AddressFor(launch.BrokerEndpoint),
            new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, ct) =>
                        await PluginLocalTransport.ConnectAsync(launch.BrokerEndpoint, ct),
                    EnableMultipleHttp2Connections = true,
                },
            }
        );

        _broker = _channel.CreateGrpcService<IPluginBrokerService>();
    }

    public Task<PluginCallResponse> CallAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _broker.CallAsync(request, Signed(context));

    public Task<PluginCallResponse> PublishAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _broker.PublishAsync(request, Signed(context));

    public IAsyncEnumerable<PluginCallRequest> SubscribeAsync(
        PluginCallRequest request,
        CallContext context = default
    ) => _broker.SubscribeAsync(request, Signed(context));

    /// <summary>
    /// The token goes on the call the caller made, not on a fresh one. A new
    /// context would drop the caller's own deadline and cancellation, and a
    /// facade that gave up would keep the server working.
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
