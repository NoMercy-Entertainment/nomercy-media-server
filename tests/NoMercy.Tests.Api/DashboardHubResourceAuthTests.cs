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
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Hubs;
using NoMercy.Api.WebSockets;
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Networking.Messaging;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

// StartResources()/StopResources() used to be reachable by any authenticated
// dashboardHub client — a merely-allowed, non-moderator user could toggle the
// shared resource-monitor and log-broadcast services for every connected
// dashboard client. Both methods now require the same "moderators" tier the
// hub already grants group membership to on connect (DashboardHub.cs ~line
// 52). These tests build a real DashboardHub against the app's actual
// DI-configured MediaContext/UserCache (via NoMercyApiFactory) and mock only
// the SignalR plumbing (HubCallerContext) plus the two gated services.
[Trait("Category", "Characterization")]
public class DashboardHubResourceAuthTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public DashboardHubResourceAuthTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        // Force the test host to start so UserCache.Current is populated with
        // the seeded DefaultUserId (Owner=true, Manage=true) and SecondaryUserId
        // (Allowed=true, Owner=false, Manage=false) test users.
        _factory.CreateClient();
    }

    private DashboardHub CreateHub(
        Guid callerUserId,
        out Mock<IResourceMonitorService> resourceMonitorService,
        out Mock<ILogBroadcastService> logBroadcastService
    )
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/dashboardHub";

        resourceMonitorService = new();
        logBroadcastService = new();

        DashboardHub hub = new(
            NullLogger<DashboardHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            // A fresh, empty ConnectedClients so StopResources' "last dashboard
            // connection" check always evaluates true, isolated from any other
            // test's device state.
            new ConnectedClients(),
            Mock.Of<IClientMessenger>(),
            logBroadcastService.Object,
            resourceMonitorService.Object,
            Mock.Of<IActivityLogger>()
        );

        ClaimsPrincipal principal = new(
            new ClaimsIdentity(
                [new(ClaimTypes.NameIdentifier, callerUserId.ToString())],
                "TestAuth"
            )
        );

        Mock<HubCallerContext> context = new();
        context.Setup(c => c.User).Returns(principal);
        context.Setup(c => c.ConnectionId).Returns(Guid.NewGuid().ToString());
        context.Setup(c => c.ConnectionAborted).Returns(CancellationToken.None);

        hub.Context = context.Object;
        hub.Clients = Mock.Of<IHubCallerClients>();

        return hub;
    }

    [Fact]
    public void StartResources_StartsMonitor_WhenCallerIsModerator()
    {
        DashboardHub hub = CreateHub(
            TestAuthHandler.DefaultUserId,
            out Mock<IResourceMonitorService> resourceMonitorService,
            out _
        );

        HubCommandResult result = hub.StartResources();

        result.Ok.Should().BeTrue();
        resourceMonitorService.Verify(s => s.Start(), Times.Once);
    }

    [Fact]
    public void StartResources_DoesNotStartMonitor_WhenCallerIsNonModeratorSecondaryUser()
    {
        // SecondaryUserId: Allowed=true, Owner=false, Manage=false — the seeded
        // "merely authenticated" tier that must no longer be able to start the
        // shared resource monitor for every connected dashboard client.
        DashboardHub hub = CreateHub(
            TestAuthHandler.SecondaryUserId,
            out Mock<IResourceMonitorService> resourceMonitorService,
            out _
        );

        HubCommandResult result = hub.StartResources();

        result.ErrorCode.Should().Be("forbidden");
        resourceMonitorService.Verify(s => s.Start(), Times.Never);
    }

    [Fact]
    public void StopResources_StopsServices_WhenCallerIsModerator()
    {
        DashboardHub hub = CreateHub(
            TestAuthHandler.DefaultUserId,
            out Mock<IResourceMonitorService> resourceMonitorService,
            out Mock<ILogBroadcastService> logBroadcastService
        );

        HubCommandResult result = hub.StopResources();

        result.Ok.Should().BeTrue();
        resourceMonitorService.Verify(s => s.Stop(), Times.Once);
        logBroadcastService.Verify(s => s.Stop(), Times.Once);
    }

    [Fact]
    public void StopResources_DoesNotStopServices_WhenCallerIsNonModeratorSecondaryUser()
    {
        DashboardHub hub = CreateHub(
            TestAuthHandler.SecondaryUserId,
            out Mock<IResourceMonitorService> resourceMonitorService,
            out Mock<ILogBroadcastService> logBroadcastService
        );

        HubCommandResult result = hub.StopResources();

        result.ErrorCode.Should().Be("forbidden");
        resourceMonitorService.Verify(s => s.Stop(), Times.Never);
        logBroadcastService.Verify(s => s.Stop(), Times.Never);
    }

    // Minimal IHttpContextAccessor stand-in — the real implementation is an
    // AsyncLocal-backed singleton unsuited to constructing an isolated
    // HttpContext per test.
    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
