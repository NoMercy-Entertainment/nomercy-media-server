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
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.Middleware;
using NoMercy.Api.Security;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class AbuseGuardMiddlewareTests
{
    private static DefaultHttpContext CreateContext(
        string path,
        string remoteIp,
        string? forwardedFor = null
    )
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        if (forwardedFor is not null)
            context.Request.Headers["CF-Connecting-IP"] = forwardedFor;

        return context;
    }

    private static Mock<IAbuseGuard> GuardThatBans(bool banned)
    {
        Mock<IAbuseGuard> guard = new();
        guard
            .Setup(x => x.IsBannedAsync(It.IsAny<IPAddress?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(banned);
        return guard;
    }

    [Fact]
    public async Task Invoke_BannedAddress_Returns403AndNeverCallsTheRestOfThePipeline()
    {
        Mock<IAbuseGuard> guard = GuardThatBans(true);
        bool nextRan = false;
        AbuseGuardMiddleware middleware = new(
            _ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            },
            Mock.Of<ILogger<AbuseGuardMiddleware>>()
        );
        DefaultHttpContext context = CreateContext("/api/v1/movies", "127.0.0.1", "203.0.113.77");

        await middleware.InvokeAsync(context, guard.Object);

        nextRan.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Invoke_CleanRequest_PassesThroughAndRecordsTheOutcome()
    {
        Mock<IAbuseGuard> guard = GuardThatBans(false);
        AbuseGuardMiddleware middleware = new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            Mock.Of<ILogger<AbuseGuardMiddleware>>()
        );
        DefaultHttpContext context = CreateContext("/status", "203.0.113.9");

        await middleware.InvokeAsync(context, guard.Object);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        guard.Verify(
            x =>
                x.RecordAsync(
                    It.IsAny<IPAddress?>(),
                    It.Is<RequestOutcome>(outcome =>
                        outcome.Path == "/status" && outcome.StatusCode == 200
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Invoke_ResolvesTheForwardedAddressNotTheRelay()
    {
        Mock<IAbuseGuard> guard = GuardThatBans(false);
        AbuseGuardMiddleware middleware = new(
            _ => Task.CompletedTask,
            Mock.Of<ILogger<AbuseGuardMiddleware>>()
        );
        DefaultHttpContext context = CreateContext("/wp-login.php", "127.0.0.1", "203.0.113.77");

        await middleware.InvokeAsync(context, guard.Object);

        guard.Verify(
            x =>
                x.RecordAsync(
                    It.Is<IPAddress?>(address => address!.ToString() == "203.0.113.77"),
                    It.IsAny<RequestOutcome>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Invoke_UnroutedRequest_ReportsThatNoEndpointMatched()
    {
        Mock<IAbuseGuard> guard = GuardThatBans(false);
        AbuseGuardMiddleware middleware = new(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            Mock.Of<ILogger<AbuseGuardMiddleware>>()
        );
        DefaultHttpContext context = CreateContext("/wp-login.php", "203.0.113.10");

        await middleware.InvokeAsync(context, guard.Object);

        guard.Verify(
            x =>
                x.RecordAsync(
                    It.IsAny<IPAddress?>(),
                    It.Is<RequestOutcome>(outcome =>
                        !outcome.EndpointMatched && !outcome.IsAuthenticated
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task Invoke_PipelineThrows_NeverScoresAndRethrows()
    {
        Mock<IAbuseGuard> guard = GuardThatBans(false);
        AbuseGuardMiddleware middleware = new(
            _ => throw new InvalidOperationException("boom"),
            Mock.Of<ILogger<AbuseGuardMiddleware>>()
        );
        DefaultHttpContext context = CreateContext("/api/v1/movies", "203.0.113.10");

        Func<Task> act = () => middleware.InvokeAsync(context, guard.Object);

        await act.Should().ThrowAsync<InvalidOperationException>();
        guard.Verify(
            x =>
                x.RecordAsync(
                    It.IsAny<IPAddress?>(),
                    It.IsAny<RequestOutcome>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }
}
