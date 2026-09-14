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

using System.Net;
using System.Net.Sockets;

namespace NoMercy.Networking.Http;

/// <summary>
/// Picks the wildcard address Kestrel binds the media port on. One dual-mode IPv6
/// socket answers both IPv4 and IPv6 clients; a second IPv4 socket on the same port
/// would collide with it on Linux. When the host cannot bind "::" at all (IPv6 disabled
/// in the OS, a Docker network without IPv6), the server must still come up on IPv4
/// exactly as it always has, so the choice is proven by a real bind, never assumed.
/// </summary>
public static class ListenAddresses
{
    public static IPAddress Wildcard() => Wildcard(CanBindDualStack);

    internal static IPAddress Wildcard(Func<bool> canBindDualStack) =>
        canBindDualStack() ? IPAddress.IPv6Any : IPAddress.Any;

    /// <summary>
    /// Binds a throwaway dual-mode socket to an ephemeral port. IPv4-mapped addresses
    /// (::ffff:a.b.c.d) are what a dual-mode listener hands every existing IPv4 client,
    /// and every IP check in the server already normalizes those back to IPv4.
    /// </summary>
    internal static bool CanBindDualStack()
    {
        if (!Socket.OSSupportsIPv6)
            return false;

        try
        {
            using Socket socket = new(
                AddressFamily.InterNetworkV6,
                SocketType.Stream,
                ProtocolType.Tcp
            );
            socket.DualMode = true;
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
