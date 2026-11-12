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

namespace NoMercy.Networking.Http;

/// <summary>
/// A CIDR range, or a single address written without a prefix.
/// </summary>
/// <remarks>
/// Not <c>System.Net.IPNetwork</c>: the transitive IPNetwork2 package declares a
/// type of the same full name, so every use of the framework type inside this
/// assembly is ambiguous. Owning a small range type here is cheaper than pinning
/// and aliasing a transitive package across the whole solution.
/// </remarks>
public readonly record struct IpRange(IPAddress Network, int PrefixLength)
{
    public static bool TryParse(string? value, out IpRange range)
    {
        range = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        int slash = trimmed.IndexOf('/');

        if (slash < 0)
        {
            if (!IPAddress.TryParse(trimmed, out IPAddress? single))
                return false;

            range = new(single, single.GetAddressBytes().Length * 8);
            return true;
        }

        if (!IPAddress.TryParse(trimmed[..slash], out IPAddress? network))
            return false;

        if (!int.TryParse(trimmed[(slash + 1)..], out int prefixLength))
            return false;

        int maximumPrefix = network.GetAddressBytes().Length * 8;
        if (prefixLength < 0 || prefixLength > maximumPrefix)
            return false;

        range = new(network, prefixLength);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (candidate.AddressFamily != Network.AddressFamily)
            return false;

        byte[] candidateBytes = candidate.GetAddressBytes();
        byte[] networkBytes = Network.GetAddressBytes();

        int wholeBytes = PrefixLength / 8;
        int remainingBits = PrefixLength % 8;

        for (int index = 0; index < wholeBytes; index++)
            if (candidateBytes[index] != networkBytes[index])
                return false;

        if (remainingBits == 0)
            return true;

        int mask = 0xFF << (8 - remainingBits);
        return (candidateBytes[wholeBytes] & mask) == (networkBytes[wholeBytes] & mask);
    }
}
