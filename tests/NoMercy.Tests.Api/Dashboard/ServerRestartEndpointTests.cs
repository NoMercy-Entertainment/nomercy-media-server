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
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoMercy.Api.Services;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

[Trait("Category", "DashboardServer")]
public sealed class ServerRestartEndpointTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _owner = factory.CreateClient().AsAuthenticated();

    [Fact]
    public async Task Restart_AnswersAcceptedWithTheRestartMode()
    {
        HttpResponseMessage response = await _owner.PostAsync(
            "/api/v1/dashboard/server/restart",
            new StringContent("{}", Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetString().Should().Be("restarting");
        body.RootElement.GetProperty("mode").GetString().Should().BeOneOf("supervisor", "relaunch");
    }

    [Fact]
    public async Task Restart_HappensAfterTheResponseIsSent()
    {
        RecordingServerRestarter restarter = new() { Outcome = new("supervisor", null) };

        HttpResponseMessage response = await ClientWith(restarter)
            .PostAsync("/api/v1/dashboard/server/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("mode").GetString().Should().Be("supervisor");
        ServerRestartOutcome completed = await restarter.Completed.WaitAsync(
            TimeSpan.FromSeconds(10)
        );
        completed.Mode.Should().Be("supervisor");
    }

    [Fact]
    public async Task Restart_WhenTheRelaunchFails_AnswersServerErrorAndKeepsRunning()
    {
        RecordingServerRestarter restarter = new()
        {
            Outcome = new("relaunch", "The new server process did not start."),
        };

        HttpResponseMessage response = await ClientWith(restarter)
            .PostAsync("/api/v1/dashboard/server/restart", null);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("The new server process did not start.");
        restarter.Completed.IsCompleted.Should().BeFalse();
    }

    private HttpClient ClientWith(RecordingServerRestarter restarter) =>
        factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IServerRestarter>();
                    services.AddSingleton<IServerRestarter>(restarter);
                })
            )
            .CreateClient()
            .AsAuthenticated();

    [Fact]
    public async Task Activity_ReturnsTheSharedCounts()
    {
        HttpResponseMessage response = await _owner.GetAsync("/api/v1/dashboard/server/activity");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("active_streams").GetInt32().Should().Be(0);
        body.RootElement.GetProperty("active_encodes")
            .GetInt32()
            .Should()
            .BeGreaterThanOrEqualTo(0);
        body.RootElement.GetProperty("can_interrupt_safely").GetBoolean().Should().BeTrue();
    }
}
