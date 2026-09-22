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

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Grpc.Server;

namespace NoMercy.PluginSdk.OutOfProcess;

/// <summary>
/// The server's end of one plugin's channel, listening.
/// <para>
/// One endpoint per plugin rather than one shared one. A shared endpoint would
/// have to trust the plugin id on every call to know who was asking, and a
/// compromised child could borrow another plugin's capabilities by typing its
/// id. Here the socket itself says which plugin it is.
/// </para>
/// <para>
/// A named pipe on Windows and a unix socket elsewhere, never a TCP port: a
/// port is reachable from the network the server is not always allowed to
/// answer on, and this endpoint hands out the owner's library.
/// </para>
/// </summary>
public sealed class PluginBrokerEndpoint(Ulid pluginId, string token, IPluginBrokerService broker)
    : IAsyncDisposable
{
    private WebApplication? _app;

    public string Endpoint { get; } = PluginChannelEndpoints.BrokerFor(pluginId);

    public async Task StartAsync(CancellationToken ct = default)
    {
        PluginLocalTransport.ClearStaleEndpoint(Endpoint);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.ConfigureKestrel(options =>
        {
            if (Software.IsWindows)
            {
                options.ListenNamedPipe(Endpoint, listen => listen.Protocols = HttpProtocols.Http2);
                return;
            }

            options.ListenUnixSocket(Endpoint, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddSingleton(broker);

        _app = builder.Build();

        // The endpoint is local, and local is not the same as ours. Without
        // this a second plugin on the same machine opens this pipe and calls
        // as the first one, with the first one's grants.
        _app.Use(
            async (context, next) =>
            {
                if (context.Request.Headers[PluginChannelEnvironment.TokenHeader] != token)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            }
        );

        _app.MapGrpcService<IPluginBrokerService>();

        await _app.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        PluginLocalTransport.ClearStaleEndpoint(Endpoint);
    }
}
