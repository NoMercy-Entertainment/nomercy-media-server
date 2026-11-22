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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NoMercy.Api.Filters;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Filters;

[Trait("Category", "Unit")]
public class SessionRequestHttpLogFilterTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string DeviceId = "01HZZZZZZZZZZZZZZZZZZZZZZZ";
    private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJl";

    private static async Task<CapturingLogger<SessionRequestHttpLogFilter>> Run(
        string method,
        int status
    )
    {
        CapturingLogger<SessionRequestHttpLogFilter> log = new();
        DefaultHttpContext http = new();
        http.Request.Method = method;
        http.Request.Path = $"/api/v1/cast/{DeviceId}/launch";
        http.Request.QueryString = new($"?access_token={Token}");
        http.User = new(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "test")
        );
        RouteData route = new(new RouteValueDictionary { ["deviceId"] = DeviceId });
        ActionContext actionContext = new(http, route, new ActionDescriptor());
        ActionExecutingContext executing = new(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            new object()
        );

        await new SessionRequestHttpLogFilter(log).OnActionExecutionAsync(
            executing,
            () =>
                Task.FromResult(
                    new ActionExecutedContext(actionContext, [], new object())
                    {
                        Result = new StatusCodeResult(status),
                    }
                )
        );
        return log;
    }

    [Fact]
    public async Task OnActionExecutionAsync_PostRequest_LogsRequestAndResultLineWithoutQueryString()
    {
        CapturingLogger<SessionRequestHttpLogFilter> log = await Run("POST", 200);

        log.Entries.Should().HaveCount(2);
        log.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Information);
        log.Entries[0]
            .Message.Should()
            .Contain($"POST /api/v1/cast/{DeviceId}/launch")
            .And.Contain($"user={UserId}")
            .And.Contain($"device={DeviceId}");
        log.Entries[1].Message.Should().Contain("status=200").And.MatchRegex(@"in \d+ms");
        string.Join('\n', log.Entries.Select(entry => entry.Message)).Should().NotContain(Token);
    }

    [Theory]
    [InlineData(404, LogLevel.Warning)]
    [InlineData(503, LogLevel.Error)]
    public async Task OnActionExecutionAsync_FailureStatus_RaisesTheResultLevel(
        int status,
        LogLevel expected
    )
    {
        CapturingLogger<SessionRequestHttpLogFilter> log = await Run("POST", status);

        log.Entries[1].Level.Should().Be(expected);
        log.Entries[1].Message.Should().Contain($"status={status}");
    }

    [Fact]
    public async Task OnActionExecutionAsync_GetRequest_LogsDebug()
    {
        CapturingLogger<SessionRequestHttpLogFilter> log = await Run("GET", 200);

        log.Entries.Should().OnlyContain(entry => entry.Level == LogLevel.Debug);
    }
}
