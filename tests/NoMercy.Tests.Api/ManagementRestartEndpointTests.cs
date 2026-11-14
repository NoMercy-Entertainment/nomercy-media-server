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
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoMercy.Api.Services;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

public sealed class ManagementRestartEndpointTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    [Fact]
    public async Task Restart_UsesTheRestarterAndPreservesTheManagementResponse()
    {
        RecordingServerRestarter restarter = new();

        HttpResponseMessage response = await ClientWith(restarter).PostAsync("/manage/restart", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("Server is restarting", body.RootElement.GetProperty("message").GetString());
        ServerRestartOutcome completed = await restarter.Completed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ServerRestartOutcome.Relaunch, completed.Mode);
    }

    [Fact]
    public async Task Restart_WhenRelaunchFails_ReportsFailureAndKeepsRunning()
    {
        RecordingServerRestarter restarter = new()
        {
            Outcome = new(ServerRestartOutcome.Relaunch, "The new server process did not start."),
        };

        HttpResponseMessage response = await ClientWith(restarter).PostAsync("/manage/restart", null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("The new server process did not start.", await response.Content.ReadAsStringAsync());
        Assert.False(restarter.Completed.IsCompleted);
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
            .CreateClient();
}
