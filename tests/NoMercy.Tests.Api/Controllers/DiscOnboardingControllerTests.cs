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

using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Media;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Onboarding;
using Xunit;

namespace NoMercy.Tests.Api.Controllers;

[Trait("Category", "Unit")]
public class DiscOnboardingControllerTests
{
    [Fact]
    public async Task StartOnboarding_UnknownDrive_ReturnsNotFound()
    {
        Mock<IDriveMonitor> driveMonitor = new();
        driveMonitor.Setup(m => m.GetDrives()).Returns([]);

        DiscOnboardingController controller = new(
            driveMonitor.Object,
            store: new DiscOnboardingSessionStore(),
            orchestrator: null!,
            libraryRepository: null!
        );

        IActionResult result = await controller.StartOnboarding(
            "Z:\\",
            null,
            CancellationToken.None
        );

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetOnboardingState_NoActiveSession_ReturnsNotFound()
    {
        DiscOnboardingSessionStore store = new();
        DiscOnboardingController controller = new(
            Mock.Of<IDriveMonitor>(),
            store,
            orchestrator: null!,
            libraryRepository: null!
        );

        IActionResult result = controller.GetOnboardingState("D:\\");

        result.Should().BeOfType<ObjectResult>();
        ((ObjectResult)result).StatusCode.Should().Be(404);
    }

    [Fact]
    public void GetOnboardingState_ActiveSession_ReturnsSessionPayload()
    {
        DiscOnboardingSessionStore store = new();
        store.Set(DiscOnboardingSession.Create("D:\\"));
        DiscOnboardingController controller = new(
            Mock.Of<IDriveMonitor>(),
            store,
            orchestrator: null!,
            libraryRepository: null!
        );

        IActionResult result = controller.GetOnboardingState("D:\\");

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().NotBeNull();
    }
}
