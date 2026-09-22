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
using NoMercy.NmSystem.Information;

namespace NoMercy.PluginSdk.Ipc;

/// <summary>
/// How the two processes reach each other, and the one place that decides it.
/// <para>
/// A named pipe on Windows and a unix socket elsewhere, never a TCP port. A
/// port is reachable from the network this process is not allowed to serve on,
/// and a plugin confined to its own data folder would be answering the house.
/// </para>
/// </summary>
public static class PluginLocalTransport
{
    /// <summary>
    /// The address gRPC is given. Nothing is fetched from it: the connect
    /// callback replaces the transport entirely, and the scheme only has to be
    /// one the client accepts.
    /// </summary>
    public static Uri AddressFor(string endpoint) =>
        new($"http://{(Software.IsWindows ? "pipe" : "socket")}.nomercy.invalid");

    /// <summary>
    /// Opens the one connection the address stands for.
    /// <para>
    /// The timeout is the caller's. A connect that waits forever turns a
    /// plugin process that never came up into a server call that never
    /// returns, which reads to the owner as the whole dashboard hanging.
    /// </para>
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        if (Software.IsWindows)
        {
            NamedPipeClientStream pipe = new(
                ".",
                endpoint,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );

            await pipe.ConnectAsync(ct);
            return pipe;
        }

        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>
    /// Clears whatever a previous run left behind.
    /// <para>
    /// A unix socket is a file, and binding onto one that still exists fails
    /// with address-in-use. A plugin that crashed would then never start
    /// again, and the reason would name neither the plugin nor the crash.
    /// </para>
    /// </summary>
    public static void ClearStaleEndpoint(string endpoint)
    {
        if (Software.IsWindows)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(endpoint)!);

        if (File.Exists(endpoint))
        {
            File.Delete(endpoint);
        }
    }
}
