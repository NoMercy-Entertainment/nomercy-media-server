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

using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NoMercy.Api.WebSockets;
using NoMercy.Service;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Every admin hub method a client can call refuses a signed-in user without the Manage
/// permission (the secondary test user, like a family member) with a forbidden
/// <c>HubCommandResult</c>, a normal completion the web handles, not a hub error.
/// The owner gets past the same gate. The dashboard's resource monitor and log
/// broadcast are stand-ins: the real monitor probes the machine's hardware.
/// </summary>
[Trait("Category", "Authorization")]
public sealed class AdminHubNonModeratorRefusalTests : IClassFixture<NoMercyApiFactory>, IDisposable
{
    private readonly Mock<IResourceMonitorService> _resourceMonitor = new();
    private readonly Mock<ILogBroadcastService> _logBroadcast = new();
    private readonly WebApplicationFactory<Startup> _factory;

    public AdminHubNonModeratorRefusalTests(NoMercyApiFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IResourceMonitorService>();
                services.AddSingleton(_resourceMonitor.Object);
                services.RemoveAll<ILogBroadcastService>();
                services.AddSingleton(_logBroadcast.Object);
            })
        );
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> InvokeAsync(
        string hubPath,
        Guid? userId,
        string method,
        params object?[] args
    )
    {
        await using HubConnection connection = await HubTestConnection.ConnectAsync(
            _factory,
            hubPath,
            userId
        );
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        return await connection.InvokeCoreAsync<JsonElement>(method, args, timeout.Token);
    }

    public static TheoryData<string, string, object?[]> AdminHubMethods =>
        new()
        {
            { "dashboardHub", "StartResources", [] },
            { "dashboardHub", "StopResources", [] },
            { "ripperHub", "GetDriveState", ["Z:/nomercy-test-drive"] },
        };

    [Theory]
    [MemberData(nameof(AdminHubMethods))]
    public async Task NonModerator_IsRefused_WithAForbiddenResult(
        string hubPath,
        string method,
        object?[] args
    )
    {
        JsonElement result = await InvokeAsync(
            hubPath,
            TestAuthHandler.SecondaryUserId,
            method,
            args
        );

        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error_code").GetString().Should().Be("forbidden");
        // Stop is not checked: the last dashboard client leaving stops the shared
        // services by design, whatever its role (DashboardHub.OnDisconnectedAsync).
        _resourceMonitor.Verify(monitor => monitor.Start(), Times.Never);
        _logBroadcast.Verify(broadcast => broadcast.Start(), Times.Never);
    }

    [Fact]
    public async Task Owner_StopsDashboardResources()
    {
        JsonElement result = await InvokeAsync("dashboardHub", null, "StopResources");

        result.GetProperty("ok").GetBoolean().Should().BeTrue(result.ToString());
    }

    [Fact]
    public async Task Owner_GetsPastTheGate_ToTheDriveLookup()
    {
        JsonElement result = await InvokeAsync(
            "ripperHub",
            null,
            "GetDriveState",
            "Z:/nomercy-test-drive"
        );

        result.GetProperty("error_code").GetString().Should().Be("not_found");
    }
}
