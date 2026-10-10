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
using NoMercy.Data.Activity;
using NoMercy.Database;
using NoMercy.Networking.Messaging;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

// GetDriveState is the only client-invokable method on RipperHub. It requires
// moderator, then resolves the caller's drivePath against the live drive list
// and, when a disc is present, probes it through a per-disc-type IDiscSource.
// These tests build a real RipperHub against the app's actual DI-configured
// MediaContext/UserCache (via NoMercyApiFactory) with IDriveMonitor and
// IDiscSource fully mocked — no real drive, disc, or probe ever runs.
[Trait("Category", "Characterization")]
public class RipperHubGetDriveStateTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public RipperHubGetDriveStateTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        // Force the test host to start so UserCache.Current is populated with
        // the seeded DefaultUserId (Owner=true, Manage=true -> moderator) and
        // SecondaryUserId (Allowed=true, Owner=false, Manage=false -> not moderator).
        _factory.CreateClient();
    }

    private RipperHub CreateHub(
        Guid callerUserId,
        Mock<IDriveMonitor> driveMonitor,
        Mock<IDiscSource>? discSource = null
    )
    {
        IDbContextFactory<MediaContext> contextFactory = _factory.Services.GetRequiredService<
            IDbContextFactory<MediaContext>
        >();

        DefaultHttpContext httpContext = new() { RequestServices = null! };
        httpContext.Request.Path = "/ripperHub";

        DiscSourceFactory discSourceFactory = new(discSource is null ? [] : [discSource.Object]);

        RipperHub hub = new(
            NullLogger<RipperHub>.Instance,
            new HttpContextAccessorStub(httpContext),
            contextFactory,
            new ConnectedClients(),
            Mock.Of<IActivityLogger>(),
            driveMonitor.Object,
            discSourceFactory
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

    private static object? GetProp(object? source, string propertyName) =>
        source?.GetType().GetProperty(propertyName)?.GetValue(source);

    [Fact]
    public async Task GetDriveState_ReturnsNull_WhenCallerIsNotModerator()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Movie Disc", true, OpticalDiscType.Dvd)]);

        RipperHub hub = CreateHub(TestAuthHandler.SecondaryUserId, driveMonitor);

        object? result = await hub.GetDriveState("D:\\");

        ((HubCommandResult)result!).ErrorCode.Should().Be("forbidden");
        // The moderator gate must reject before the drive list is even read.
        driveMonitor.Verify(m => m.GetDrives(), Times.Never);
    }

    [Fact]
    public async Task GetDriveState_ReturnsNull_WhenDrivePathMatchesNoDrive()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Movie Disc", true, OpticalDiscType.Dvd)]);
        Mock<IDiscSource> discSource = new();
        discSource.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor, discSource);

        object? result = await hub.GetDriveState("Z:\\");

        ((HubCommandResult)result!).ErrorCode.Should().Be("not_found");
        discSource.Verify(
            s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetDriveState_EmptyDrivePath_NoDrivesPresent_ReturnsNull_WithoutException()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns([]);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor);

        object? result = await hub.GetDriveState("");

        ((HubCommandResult)result!).ErrorCode.Should().Be("invalid_input");
    }

    [Fact]
    public async Task GetDriveState_WhitespaceDrivePath_MatchesNoDrive_ReturnsNull()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Empty Tray", false, OpticalDiscType.None)]);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor);

        object? result = await hub.GetDriveState("   ");

        ((HubCommandResult)result!).ErrorCode.Should().Be("invalid_input");
    }

    [Fact]
    public async Task GetDriveState_NullDrivePath_NoDrivesPresent_ReturnsNull_WithoutException()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns([]);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor);

        object? result = await hub.GetDriveState(null!);

        ((HubCommandResult)result!).ErrorCode.Should().Be("invalid_input");
    }

    [Fact]
    public async Task GetDriveState_NullDrivePath_WithDrivesPresent_ReturnsNull()
    {
        // A null/blank drivePath is guarded before the TrimEnd inside the matching
        // predicate. Previously this threw NullReferenceException the moment a drive
        // was attached (the empty-drive-list case was safe only because
        // FirstOrDefault skips the predicate on an empty sequence); it now returns
        // null gracefully instead of surfacing an unhandled exception.
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Empty Tray", false, OpticalDiscType.None)]);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor);

        object? result = await hub.GetDriveState(null!);

        ((HubCommandResult)result!).ErrorCode.Should().Be("invalid_input");
    }

    [Fact]
    public async Task GetDriveState_KnownDriveWithoutDisc_ReturnsDriveInfo_WithoutProbingSource()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Empty Tray", false, OpticalDiscType.None)]);
        Mock<IDiscSource> discSource = new();
        discSource.Setup(s => s.Type).Returns(OpticalDiscType.None);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor, discSource);

        object? result = await hub.GetDriveState("D:\\");

        result.Should().NotBeNull();
        GetProp(result, "has_disc").Should().Be(false);
        GetProp(result, "open").Should().Be(true);
        GetProp(result, "disc_type").Should().Be("none");
        discSource.Verify(
            s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task GetDriveState_KnownDriveWithDisc_NoRegisteredSource_ReturnsDriveInfoWithoutProbe()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor
            .Setup(m => m.GetDrives())
            .Returns([new DiscDrive("D:\\", "Mystery Disc", true, OpticalDiscType.Cd)]);

        // No IDiscSource registered for OpticalDiscType.Cd -> DiscSourceFactory.CreateFor
        // returns null and the probe branch must never be reached.
        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor);

        object? result = await hub.GetDriveState("D:\\");

        result.Should().NotBeNull();
        GetProp(result, "has_disc").Should().Be(true);
        GetProp(result, "open").Should().Be(false);
        GetProp(result, "disc_type").Should().Be("cd");
        GetProp(result, "label").Should().Be("Mystery Disc");
    }

    [Fact]
    public async Task GetDriveState_KnownDriveWithDisc_RegisteredSource_ReturnsProbedDiscInfo()
    {
        DiscDrive drive = new("D:\\", "Fallback Label", true, OpticalDiscType.Dvd);
        DiscInfo discInfo = new(
            OpticalDiscType.Dvd,
            "DISC_LABEL",
            [],
            null,
            TimeSpan.FromMinutes(90),
            DiscTitle: "The Movie"
        );

        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns([drive]);

        Mock<IDiscSource> discSource = new();
        discSource.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        discSource
            .Setup(s => s.ProbeAsync(drive, It.IsAny<CancellationToken>()))
            .ReturnsAsync(discInfo);

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor, discSource);

        object? result = await hub.GetDriveState("D:\\");

        result.Should().NotBeNull();
        GetProp(result, "path").Should().Be("D:");
        GetProp(result, "label").Should().Be("The Movie");
        GetProp(result, "has_disc").Should().Be(true);
        GetProp(result, "open").Should().Be(false);
        GetProp(result, "disc_type").Should().Be("dvd");
        discSource.Verify(s => s.ProbeAsync(drive, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDriveState_ProbeThrows_ReturnsFallbackDriveInfo_WithoutDiscField()
    {
        DiscDrive drive = new("D:\\", "Fallback Label", true, OpticalDiscType.BluRay);

        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns([drive]);

        Mock<IDiscSource> discSource = new();
        discSource.Setup(s => s.Type).Returns(OpticalDiscType.BluRay);
        discSource
            .Setup(s => s.ProbeAsync(drive, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("drive busy"));

        RipperHub hub = CreateHub(TestAuthHandler.DefaultUserId, driveMonitor, discSource);

        object? result = await hub.GetDriveState("D:\\");

        result.Should().NotBeNull();
        GetProp(result, "label").Should().Be("Fallback Label");
        GetProp(result, "has_disc").Should().Be(true);
        GetProp(result, "open").Should().Be(false);
        GetProp(result, "disc_type").Should().Be("bluray");
        // The success-only "disc" field must be absent from the exception fallback
        // shape, distinguishing it from the probed-success envelope.
        GetProp(result, "disc").Should().BeNull();
    }

    // Minimal IHttpContextAccessor stand-in — the real implementation is an
    // AsyncLocal-backed singleton unsuited to constructing an isolated
    // HttpContext per test.
    private sealed class HttpContextAccessorStub(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }
}
