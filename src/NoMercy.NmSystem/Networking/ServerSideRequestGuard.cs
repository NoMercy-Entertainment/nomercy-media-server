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

namespace NoMercy.NmSystem.Networking;

/// <summary>
/// Guards server-side fetches of caller-supplied URLs against SSRF. A URL the
/// client controls (a subtitle download link, a community-preset URL) must never
/// let the server reach its own loopback, the LAN, or cloud link-local metadata
/// (169.254.169.254). This validates the scheme and resolves the host, rejecting
/// the request when ANY resolved address is not publicly routable.
///
/// <para>Note: this is an egress allow-check, not a rebinding-proof pin. It
/// resolves and validates, then the caller connects; a hostile resolver could in
/// principle return a public address here and a private one to the HTTP client.
/// For our threat model (a low-privilege authenticated user, not a network-level
/// attacker) rejecting private/link-local resolutions closes the practical
/// internal-SSRF vector.</para>
/// </summary>
public static class ServerSideRequestGuard
{
    /// <summary>
    /// True only when <paramref name="url"/> is an absolute http(s) URL whose host
    /// resolves exclusively to publicly routable addresses.
    /// </summary>
    public static async Task<bool> IsSafePublicHttpUrlAsync(
        string? url,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return addresses.Length > 0 && addresses.All(IsPubliclyRoutable);
    }

    /// <summary>For caller-supplied URLs: checks every connection, so a redirect or a second DNS answer cannot reach a private host.</summary>
    public static SocketsHttpHandler CreatePublicOnlyHandler() =>
        new() { ConnectCallback = ConnectPublicOnlyAsync };

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        string host = context.DnsEndPoint.Host;
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        if (addresses.Length == 0 || !addresses.All(IsPubliclyRoutable))
            throw new HttpRequestException(
                $"Refused to connect to {host}: it is not publicly routable."
            );

        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// False for loopback, private (RFC 1918), CGNAT (100.64/10), link-local
    /// (169.254/16, fe80::/10), unique-local (fc00::/7), unspecified, and
    /// multicast addresses; true for everything else.
    /// </summary>
    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] octets = address.GetAddressBytes();
            return octets[0] switch
            {
                0 => false, // 0.0.0.0/8 "this network"
                10 => false, // 10.0.0.0/8 private
                127 => false, // loopback (also caught above)
                100 when octets[1] is >= 64 and <= 127 => false, // 100.64.0.0/10 CGNAT
                169 when octets[1] == 254 => false, // 169.254.0.0/16 link-local
                172 when octets[1] is >= 16 and <= 31 => false, // 172.16.0.0/12 private
                192 when octets[1] == 168 => false, // 192.168.0.0/16 private
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;

            if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
                return false;

            // fc00::/7 unique-local
            byte[] octets = address.GetAddressBytes();
            if ((octets[0] & 0xFE) == 0xFC)
                return false;

            return true;
        }

        return false;
    }
}
