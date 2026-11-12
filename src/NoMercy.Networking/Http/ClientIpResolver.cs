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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace NoMercy.Networking.Http;

/// <summary>
/// Resolves the address that actually made a request. A relay in front of the
/// server (Cloudflare Tunnel, nginx, a container port mapping) terminates the
/// connection itself, so the transport peer is the relay — every external caller
/// then looks like 127.0.0.1. The real caller is carried in a forwarding header,
/// which is only believed when the peer is a relay we could plausibly own.
/// </summary>
public static class ClientIpResolver
{
    private const string ClientIpItemKey = "NoMercy.ClientIp";
    private const string CloudflareHeader = "CF-Connecting-IP";
    private const string ForwardedForHeader = "X-Forwarded-For";
    private const string RealIpHeader = "X-Real-IP";

    /// <summary>
    /// The caller's own address, or the transport peer when the request did not
    /// arrive through a trusted relay. Resolved once per request.
    /// </summary>
    public static IPAddress? ClientIp(this HttpContext context)
    {
        if (context.Items.TryGetValue(ClientIpItemKey, out object? cached))
            return cached as IPAddress;

        IPAddress? resolved = Resolve(context);
        context.Items[ClientIpItemKey] = resolved;
        return resolved;
    }

    /// <summary>
    /// True when the request reached the server through a relay. Loopback alone
    /// stops proving "this machine" the moment such a relay is in front of the
    /// server, so any check that grants trust to localhost must exclude these.
    /// </summary>
    public static bool IsProxied(this HttpContext context)
    {
        if (!IsTrustedProxy(context.Connection.RemoteIpAddress))
            return false;

        IHeaderDictionary headers = context.Request.Headers;
        return headers.ContainsKey(CloudflareHeader)
            || headers.ContainsKey(ForwardedForHeader)
            || headers.ContainsKey(RealIpHeader);
    }

    private static IPAddress? Resolve(HttpContext context)
    {
        IPAddress? peer = Normalize(context.Connection.RemoteIpAddress);
        if (peer is null || !IsTrustedProxy(peer))
            return peer;

        IPAddress? cloudflare = Parse(context.Request.Headers[CloudflareHeader].FirstOrDefault());
        if (cloudflare is not null)
            return cloudflare;

        IPAddress? forwarded = ResolveForwardedFor(context.Request.Headers[ForwardedForHeader]);
        if (forwarded is not null)
            return forwarded;

        return Parse(context.Request.Headers[RealIpHeader].FirstOrDefault()) ?? peer;
    }

    private static IPAddress? ResolveForwardedFor(StringValues headerValues)
    {
        List<IPAddress> chain = [];

        foreach (string? headerValue in headerValues)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
                continue;

            foreach (string entry in headerValue.Split(','))
            {
                IPAddress? parsed = Parse(entry);
                if (parsed is not null)
                    chain.Add(parsed);
            }
        }

        if (chain.Count == 0)
            return null;

        // Read right to left and strip every hop that is a relay: the chain is
        // appended to by each proxy, so the rightmost address we do not own is the
        // last one a proxy observed rather than one the caller could invent.
        for (int index = chain.Count - 1; index >= 0; index--)
            if (!IsTrustedProxy(chain[index]))
                return chain[index];

        return chain[0];
    }

    private static IPAddress? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();

        if (IPAddress.TryParse(trimmed, out IPAddress? address))
            return Normalize(address);

        // Proxies write "1.2.3.4:51820" and "[2a02::1]:51820" into the chain too.
        if (IPEndPoint.TryParse(trimmed, out IPEndPoint? endpoint))
            return Normalize(endpoint.Address);

        return null;
    }

    /// <summary>
    /// True for loopback and every range that can only exist inside a network
    /// someone runs themselves. These are the addresses a ban would be wrong
    /// about: the owner, their LAN, or their own relay.
    /// </summary>
    public static bool IsPrivateNetwork(IPAddress? address)
    {
        IPAddress? normalized = Normalize(address);
        if (normalized is null)
            return false;

        if (IPAddress.IsLoopback(normalized))
            return true;

        if (normalized.AddressFamily is AddressFamily.InterNetwork)
        {
            byte[] octets = normalized.GetAddressBytes();
            return octets[0] switch
            {
                10 => true,
                169 => octets[1] is 254,
                172 => octets[1] is >= 16 and <= 31,
                192 => octets[1] is 168,
                100 => octets[1] is >= 64 and <= 127,
                _ => false,
            };
        }

        return normalized.IsIPv6LinkLocal
            || normalized.IsIPv6UniqueLocal
            || normalized.IsIPv6SiteLocal;
    }

    /// <summary>Operator-listed relays, hydrated at boot from the row
    /// <see cref="TrustedProxyList.ConfigKey"/>; empty keeps the private-range rule.</summary>
    public static IReadOnlyList<IpRange> TrustedProxies { get; set; } = [];

    private static bool IsTrustedProxy(IPAddress? address)
    {
        IReadOnlyList<IpRange> trusted = TrustedProxies;
        if (trusted.Count == 0)
            return IsPrivateNetwork(address);

        IPAddress? normalized = Normalize(address);
        return normalized is not null && trusted.Any(range => range.Contains(normalized));
    }

    private static IPAddress? Normalize(IPAddress? address)
    {
        if (address is null)
            return null;

        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
