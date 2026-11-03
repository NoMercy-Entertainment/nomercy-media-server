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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The owner's door for a plugin held at boot: it was put in the plugins
/// folder by hand, or changed after the last approval (issue #622).
/// </summary>
public class PluginApproveEndpointTests
{
    private const string Reason = "This plugin was put in the plugins folder by hand.";
    private static readonly Ulid HeldId = Ulid.NewUlid();
    private static readonly Ulid RunningId = Ulid.NewUlid();

    private readonly Mock<IPluginManager> _pluginManager = new();

    public PluginApproveEndpointTests()
    {
        _pluginManager
            .Setup(manager => manager.GetInstalledPlugins())
            .Returns([
                new()
                {
                    Id = HeldId,
                    Name = "Dropped",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Disabled,
                    ApprovalReason = Reason,
                },
                new()
                {
                    Id = RunningId,
                    Name = "Installed",
                    Description = "d",
                    Version = new(1, 0, 0),
                    Status = PluginStatus.Active,
                },
            ]);
    }

    private PluginController BuildController() =>
        new(
            _pluginManager.Object,
            Mock.Of<IPluginConsentService>(),
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>()
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    private static PluginInfoDto Payload(IActionResult result) =>
        ((DataResponseDto<PluginInfoDto>)((OkObjectResult)result).Value!).Data!;

    [Fact]
    public async Task Approve_OnAHeldPlugin_ApprovesItThroughTheManager()
    {
        IActionResult result = await BuildController().Approve(HeldId, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _pluginManager.Verify(
            manager => manager.ApprovePluginAsync(HeldId, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task Approve_UnknownPlugin_IsNotFound()
    {
        IActionResult result = await BuildController()
            .Approve(Ulid.NewUlid(), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        _pluginManager.Verify(
            manager => manager.ApprovePluginAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Approve_OnAPluginNotWaiting_IsRefusedWithoutReachingTheManager()
    {
        IActionResult result = await BuildController().Approve(RunningId, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _pluginManager.Verify(
            manager => manager.ApprovePluginAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task Enable_OnAHeldPlugin_IsRefusedAndNeverEnables()
    {
        IActionResult result = await BuildController().Enable(HeldId);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _pluginManager.Verify(
            manager => manager.EnablePluginAsync(HeldId, It.IsAny<CancellationToken>()),
            Times.Never,
            "enabling is not a door past the hold"
        );
    }

    [Fact]
    public async Task Enable_OnARunningPlugin_StillReachesTheManager()
    {
        await BuildController().Enable(RunningId);

        _pluginManager.Verify(
            manager => manager.EnablePluginAsync(RunningId, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public void Show_CarriesAwaitingApprovalAndTheReason()
    {
        PluginInfoDto held = Payload(BuildController().Show(HeldId));
        PluginInfoDto running = Payload(BuildController().Show(RunningId));

        held.AwaitingApproval.Should().BeTrue();
        held.ApprovalReason.Should().Be(Reason);
        held.Status.Should().Be("disabled");
        running.AwaitingApproval.Should().BeFalse();
        running.ApprovalReason.Should().BeNull();
    }
}
