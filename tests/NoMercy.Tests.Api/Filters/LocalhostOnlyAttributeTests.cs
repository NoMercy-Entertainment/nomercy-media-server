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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Api.Filters;
using Xunit;

namespace NoMercy.Tests.Api.Filters;

[Trait("Category", "Unit")]
public class LocalhostOnlyAttributeTests
{
    [Fact]
    public void OnAuthorization_LoopbackIp_AllowsRequest()
    {
        LocalhostOnlyAttribute attribute = new();
        AuthorizationFilterContext context = CreateContext(IPAddress.Loopback);

        attribute.OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void OnAuthorization_IPv6Loopback_AllowsRequest()
    {
        LocalhostOnlyAttribute attribute = new();
        AuthorizationFilterContext context = CreateContext(IPAddress.IPv6Loopback);

        attribute.OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void OnAuthorization_RemoteIp_BlocksRequest()
    {
        LocalhostOnlyAttribute attribute = new();
        AuthorizationFilterContext context = CreateContext(IPAddress.Parse("192.168.1.100"));

        attribute.OnAuthorization(context);

        Assert.NotNull(context.Result);
        JsonResult jsonResult = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(403, jsonResult.StatusCode);
    }

    [Fact]
    public void OnAuthorization_NullRemoteIp_AllowsRequest()
    {
        // Null remote IP happens with named pipes, IPC, and in-process test hosts.
        // The primary security boundary is Kestrel binding to 127.0.0.1 only.
        LocalhostOnlyAttribute attribute = new();
        AuthorizationFilterContext context = CreateContext(null);

        attribute.OnAuthorization(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void OnAuthorization_LoopbackThroughRelay_BlocksRequest()
    {
        // cloudflared terminates the tunnel locally and connects from 127.0.0.1,
        // so without the forwarded-header check the management API would be
        // reachable by anyone on the internet.
        LocalhostOnlyAttribute attribute = new();
        AuthorizationFilterContext context = CreateContext(
            IPAddress.Loopback,
            ("CF-Connecting-IP", "45.148.10.99")
        );

        attribute.OnAuthorization(context);

        Assert.NotNull(context.Result);
        JsonResult jsonResult = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(403, jsonResult.StatusCode);
    }

    private static AuthorizationFilterContext CreateContext(
        IPAddress? remoteIp,
        params (string Name, string Value)[] headers
    )
    {
        DefaultHttpContext httpContext = new() { Connection = { RemoteIpAddress = remoteIp } };

        foreach ((string name, string value) in headers)
            httpContext.Request.Headers[name] = value;

        ActionContext actionContext = new(httpContext, new(), new());

        return new(actionContext, []);
    }
}
