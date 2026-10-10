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

using Microsoft.AspNetCore.Http;

namespace NoMercy.Setup.Server;

/// <summary>
/// Decides whether the setup page may use the browser redirect login on the host it
/// was served from. Keycloak only allows redirects to *.nomercy.tv, localhost and the
/// loopback addresses; any other host (a LAN IP, a NAS name) must use the device code
/// flow instead, so the server never sends a redirect_uri it does not own.
/// </summary>
public static class TrustedSetupHost
{
    private const string NoMercyDomainSuffix = ".nomercy.tv";

    public static bool IsTrusted(HostString host)
    {
        if (!host.HasValue)
            return false;

        string name = host.Host;

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (name == "127.0.0.1" || name == "[::1]")
            return true;

        return name.Length > NoMercyDomainSuffix.Length
            && name.EndsWith(NoMercyDomainSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
