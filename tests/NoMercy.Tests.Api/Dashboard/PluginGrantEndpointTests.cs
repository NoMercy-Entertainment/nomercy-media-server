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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.PluginSdk.Verification;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Pressing Allow cleared the request and granted nothing, which is
/// indistinguishable from the site refusing the plugin: the plugin reports "the
/// server has not granted access", the owner has already pressed Allow, and no
/// screen says which of the two is true. These cases pin both halves of that -
/// the body the dashboard actually posts, and what the decision does with it.
/// </summary>
public class PluginGrantEndpointTests
{
    private readonly Mock<IPluginGrantStore> _grantStore = new();
    private readonly Mock<IPluginManager> _pluginManager = new();
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private PluginController BuildController()
    {
        _pluginManager
            .Setup(manager => manager.GetInstalledPlugins())
            .Returns([
                new()
                {
                    Id = PluginId,
                    Name = "Torrent Downloader",
                    Description = "",
                    Version = new(0, 4, 0),
                    Status = PluginStatus.Active,
                },
            ]);

        return BuildController(_pluginManager.Object);
    }

    private PluginController BuildController(IPluginManager pluginManager) =>
        new(
            pluginManager,
            Mock.Of<IPluginConsentService>(),
            _grantStore.Object,
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>()
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    /// <summary>
    /// The body the deployed dashboard posts, keys and all. It carries the whole
    /// pending row beside the decision, and a decision read off the wrong key
    /// reads as a denial - which is exactly the reported symptom.
    /// </summary>
    [Theory]
    [InlineData(
        "{\"plugin_id\":\"01J\",\"kind\":\"network.host\",\"value\":\"tracker.example\","
            + "\"reason\":\"fetch\",\"requested_at\":\"2026-08-22T02:03:00Z\",\"granted\":true}",
        true
    )]
    [InlineData("{\"kind\":\"network.host\",\"value\":\"tracker.example\",\"granted\":true}", true)]
    [InlineData(
        "{\"kind\":\"network.host\",\"value\":\"tracker.example\",\"granted\":false}",
        false
    )]
    public void ADecision_KeepsItsAnswerThroughDeserialization(string body, bool expected)
    {
        PluginGrantDecisionDto? decision = JsonConvert.DeserializeObject<PluginGrantDecisionDto>(
            body
        );

        decision.Should().NotBeNull();
        decision!.Granted.Should().Be(expected);
        decision.Kind.Should().Be(PluginGrantKind.NetworkHost);
        decision.Value.Should().Be("tracker.example");
    }

    [Fact]
    public void Allow_StoresTheGrantRatherThanClearingTheRequest()
    {
        PluginController controller = BuildController();

        IActionResult result = controller.ResolveGrant(
            PluginId,
            new()
            {
                Kind = PluginGrantKind.NetworkHost,
                Value = "tracker.example",
                Granted = true,
            }
        );

        result.Should().BeOfType<OkObjectResult>();
        _grantStore.Verify(
            store => store.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example"),
            Times.Once
        );
        _grantStore.Verify(
            store => store.ClearRequest(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Fact]
    public void Deny_ClearsTheRequestAndStoresNothing()
    {
        PluginController controller = BuildController();

        controller.ResolveGrant(
            PluginId,
            new()
            {
                Kind = PluginGrantKind.NetworkHost,
                Value = "tracker.example",
                Granted = false,
            }
        );

        _grantStore.Verify(
            store => store.ClearRequest(PluginId, PluginGrantKind.NetworkHost, "tracker.example"),
            Times.Once
        );
        _grantStore.Verify(
            store => store.Grant(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    /// <summary>
    /// Consenting takes the grants in the same call so the owner decides once.
    /// The dashboard sent none, so approving a plugin that declares hosts left
    /// every one of them ungranted.
    /// </summary>
    [Fact]
    public async Task Consent_StoresEveryGrantNamedInTheSameCall()
    {
        PluginController controller = BuildController();

        await controller.Consent(
            PluginId,
            new()
            {
                Grants =
                [
                    new() { Kind = PluginGrantKind.NetworkHost, Value = "tracker.example" },
                    new() { Kind = PluginGrantKind.NetworkHost, Value = "cdn.example" },
                ],
            }
        );

        _grantStore.Verify(
            store => store.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example"),
            Times.Once
        );
        _grantStore.Verify(
            store => store.Grant(PluginId, PluginGrantKind.NetworkHost, "cdn.example"),
            Times.Once
        );
    }
}
