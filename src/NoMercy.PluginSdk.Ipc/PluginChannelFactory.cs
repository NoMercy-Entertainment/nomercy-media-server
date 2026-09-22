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

using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Net.Client;
using NoMercy.NmSystem.Information;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// One connection to a plugin channel, over whatever this platform has.
/// <para>
/// A named pipe on Windows and a Unix socket elsewhere, both local only: a TCP
/// port would be reachable from the network the plugin is not allowed to serve
/// on. The token travels on every request, so a process that finds the pipe
/// still cannot use it.
/// </para>
/// </summary>
public static class PluginChannelFactory
{
    public static GrpcChannel Connect(string endpoint, string token)
    {
        SocketsHttpHandler handler = new()
        {
            ConnectCallback = (_, cancellationToken) => DialAsync(endpoint, cancellationToken),
            EnableMultipleHttp2Connections = true,
        };

        return GrpcChannel.ForAddress(
            "http://nomercy-plugin",
            new GrpcChannelOptions
            {
                HttpHandler = new TokenHandler(handler, token),
                DisposeHttpClient = true,
            }
        );
    }

    private static async ValueTask<Stream> DialAsync(
        string endpoint,
        CancellationToken cancellationToken
    )
    {
        if (Software.IsWindows)
        {
            NamedPipeClientStream pipe = new(
                ".",
                endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private sealed class TokenHandler(HttpMessageHandler inner, string token)
        : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.Headers.TryAddWithoutValidation(PluginChannelEnvironment.TokenHeader, token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
