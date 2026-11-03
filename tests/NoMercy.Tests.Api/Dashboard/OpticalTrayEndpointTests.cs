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
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NoMercy.NmSystem.Dto;
using NoMercy.NmSystem.SystemCalls.OpticalDrives;
using NoMercy.OpticalMedia.Drives;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// POST /dashboard/optical/{drivePath}/open|close: the route's drive path must
/// match a drive the monitor reports before any OS tool runs, and a failing
/// tool must surface as 400 instead of "Drive opened.".
/// </summary>
[Trait("Category", "OpticalMedia")]
public class OpticalTrayEndpointTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public OpticalTrayEndpointTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient BuildClient(Mock<ITrayCommands> trayCommands, params DiscDrive[] drives)
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns(drives);

        return _factory
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDriveMonitor>();
                    services.AddSingleton(driveMonitor.Object);

                    services.RemoveAll<ITrayCommands>();
                    services.AddSingleton(trayCommands.Object);
                });
            })
            .CreateClient()
            .AsAuthenticated();
    }

    private static DiscDrive KnownDrive() =>
        new("D:\\", "MOVIE", HasDisc: true, OpticalDiscType.Dvd, MountPoint: "D:\\");

    [Fact]
    public async Task Open_UnknownDrive_Returns400WithoutRunningTheTool()
    {
        Mock<ITrayCommands> trayCommands = new();
        HttpClient client = BuildClient(trayCommands, KnownDrive());

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/dashboard/optical/E%3A/open",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        trayCommands.Verify(c => c.Open(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Close_ToolFails_Returns400()
    {
        Mock<ITrayCommands> trayCommands = new();
        trayCommands.Setup(c => c.Close("D:\\")).Returns(false);
        HttpClient client = BuildClient(trayCommands, KnownDrive());

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/dashboard/optical/D%3A/close",
            null
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        trayCommands.Verify(c => c.Close("D:\\"), Times.Once);
    }

    [Fact]
    public async Task Open_KnownDrive_ToolSucceeds_Returns200()
    {
        Mock<ITrayCommands> trayCommands = new();
        trayCommands.Setup(c => c.Open("D:\\")).Returns(true);
        HttpClient client = BuildClient(trayCommands, KnownDrive());

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/dashboard/optical/D%3A/open",
            null
        );

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        trayCommands.Verify(c => c.Open("D:\\"), Times.Once);
    }
}
