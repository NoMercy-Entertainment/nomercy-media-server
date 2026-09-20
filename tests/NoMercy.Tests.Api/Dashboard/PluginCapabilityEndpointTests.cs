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
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Api.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The owner used to answer for a whole plugin at once. These pin the shape of
/// answering one capability at a time, including the case that matters most:
/// a list with one bad name must not be applied halfway, or the owner ends up
/// having approved a set they never saw.
/// </summary>
public class PluginCapabilityEndpointTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private readonly Mock<IPluginManager> _plugins = new();
    private readonly Mock<IPluginConsentService> _consent = new();

    private PluginCapabilityController BuildController(params string[] declared)
    {
        _plugins
            .Setup(manager => manager.GetPluginInfo(PluginId))
            .Returns(
                new PluginInfo
                {
                    Id = PluginId,
                    Name = "Internet Radio",
                    Description = "",
                    Version = new(2, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = new() { Hooks = [.. declared] },
                }
            );

        return new(_plugins.Object, _consent.Object, new(_consent.Object));
    }

    private static IEnumerable<PluginCapabilityStateDto> Body(IActionResult result) =>
        (
            (DataResponseDto<IEnumerable<PluginCapabilityStateDto>>)((OkObjectResult)result).Value!
        ).Data!;

    [Fact]
    public void The_list_names_every_capability_the_manifest_declares()
    {
        PluginCapabilityController controller = BuildController("network.fetch", "library.read");

        IEnumerable<PluginCapabilityStateDto> states = Body(controller.Index(PluginId));

        states
            .Select(state => state.Name)
            .Should()
            .BeEquivalentTo(["network.fetch", "library.read"]);
    }

    [Fact]
    public void Each_entry_carries_a_key_a_trust_level_and_a_docs_link()
    {
        PluginCapabilityController controller = BuildController("network.fetch");

        PluginCapabilityStateDto state = Body(controller.Index(PluginId)).Single();

        state.SummaryKey.Should().NotBeEmpty();
        state.Trust.Should().NotBeEmpty();
        state
            .DocsUrl.Should()
            .Contain("network-fetch", "an owner deciding needs somewhere to read what it means");
    }

    [Fact]
    public void A_capability_the_vocabulary_cannot_describe_is_not_shown()
    {
        PluginCapabilityController controller = BuildController(
            "network.fetch",
            "not.a.capability"
        );

        Body(controller.Index(PluginId))
            .Select(state => state.Name)
            .Should()
            .Equal(
                ["network.fetch"],
                "nobody can answer for something the server has no description of"
            );
    }

    [Fact]
    public void The_approved_flag_and_version_come_from_what_the_owner_answered()
    {
        _consent.Setup(service => service.IsApproved(PluginId, "network.fetch")).Returns(true);
        _consent
            .Setup(service => service.ApprovedAt(PluginId, "network.fetch"))
            .Returns(new System.Version(1, 0, 0));
        PluginCapabilityController controller = BuildController("network.fetch");

        PluginCapabilityStateDto state = Body(controller.Index(PluginId)).Single();

        state.Approved.Should().BeTrue();
        state
            .ApprovedAtVersion.Should()
            .Be("1.0.0", "a client shows 'approved for 1.0.0, this is 2.0.0' from these two");
    }

    [Fact]
    public void An_unknown_plugin_is_not_found_rather_than_an_empty_list()
    {
        PluginCapabilityController controller = new(
            _plugins.Object,
            _consent.Object,
            new(_consent.Object)
        );

        ((ObjectResult)controller.Index(Ulid.NewUlid()))
            .StatusCode.Should()
            .Be(404, "an empty list reads as a plugin that asks for nothing");
    }

    [Fact]
    public void Approving_one_records_it_at_the_installed_version()
    {
        PluginCapabilityController controller = BuildController("network.fetch");

        controller.Store(PluginId, [new() { Name = "network.fetch", Approved = true }]);

        _consent.Verify(
            service =>
                service.ApproveCapability(PluginId, "network.fetch", new System.Version(2, 0, 0)),
            Times.Once
        );
    }

    [Fact]
    public void Refusing_one_revokes_it()
    {
        PluginCapabilityController controller = BuildController("network.fetch");

        controller.Store(PluginId, [new() { Name = "network.fetch", Approved = false }]);

        _consent.Verify(service => service.RevokeCapability(PluginId, "network.fetch"), Times.Once);
    }

    [Fact]
    public void A_list_with_one_name_the_plugin_never_asked_for_writes_nothing()
    {
        PluginCapabilityController controller = BuildController("network.fetch");

        IActionResult result = controller.Store(
            PluginId,
            [
                new() { Name = "network.fetch", Approved = true },
                new() { Name = "process.spawn", Approved = true },
            ]
        );

        ((ObjectResult)result).StatusCode.Should().Be(422);
        _consent.Verify(
            service =>
                service.ApproveCapability(
                    It.IsAny<Ulid>(),
                    It.IsAny<string>(),
                    It.IsAny<System.Version>()
                ),
            Times.Never,
            "applied halfway, the owner approves a set they never saw"
        );
    }

    [Fact]
    public void Answering_returns_the_new_state_so_a_client_need_not_ask_again()
    {
        PluginCapabilityController controller = BuildController("network.fetch");

        IActionResult result = controller.Store(
            PluginId,
            [new() { Name = "network.fetch", Approved = true }]
        );

        Body(result).Should().ContainSingle().Which.Name.Should().Be("network.fetch");
    }
}
