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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Filters;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class LocalhostOnlyAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        IPAddress? remoteIp = context.HttpContext.Connection.RemoteIpAddress;

        // Null remote IP happens with named pipes, IPC, and in-process test hosts.
        // The primary security boundary is Kestrel binding to 127.0.0.1 only.
        if (remoteIp is null)
            return;

        // A local relay (Cloudflare Tunnel's cloudflared, nginx, a container port
        // mapping) connects from loopback on behalf of whoever is on the internet,
        // so a loopback peer carrying a forwarding header is not a local caller —
        // without this, the tunnel hands the management API to the whole internet.
        bool isLocalhost = IPAddress.IsLoopback(remoteIp) && !context.HttpContext.IsProxied();

        if (!isLocalhost)
        {
            context.Result = new JsonResult(
                new
                {
                    status = "error",
                    message = "Management API is only accessible from localhost",
                }
            )
            {
                StatusCode = 403,
            };
        }
    }
}
