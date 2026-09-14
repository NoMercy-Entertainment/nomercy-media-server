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
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;

namespace NoMercy.Setup.Cast;

/// <summary>What a Cast launch tells the receiver about where it came from.</summary>
public static class CastLaunchOrigin
{
    private const string DefaultLocale = "en-US";

    /// <summary>
    /// The public origin the receiver uses for the API and SignalR: the address
    /// connectivity resolved, or the configured base URL until it has.
    /// </summary>
    public static string ServerUrl(INetworkDiscovery? networkDiscovery)
    {
        string? external = networkDiscovery?.ExternalAddress;
        return string.IsNullOrEmpty(external)
            ? ExternalServicesConfig.Current.ApiBaseUrl
            : external;
    }

    /// <summary>The first language tag of the sender's Accept-Language header.</summary>
    public static string SenderLocale(string? acceptLanguage)
    {
        if (string.IsNullOrEmpty(acceptLanguage))
            return DefaultLocale;

        string first = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        return string.IsNullOrEmpty(first) ? DefaultLocale : first;
    }
}
