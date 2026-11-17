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
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.Plugins;
using NoMercy.Design;
using NoMercy.Plugin.Samples.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

[Trait("Category", "Unit")]
public class PluginViewComponentGuardTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y000000000000000D");
    private static readonly Guid Owner = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly PluginCaller AnyCaller = new(
        UserId.Empty,
        "Stoney",
        PluginRole.Owner,
        PluginAccess.Owned,
        "en",
        PluginSurface.Web
    );

    /// <summary>The app components a plugin may draw (the web client's PLUGIN_APP_COMPONENTS).</summary>
    private static readonly string[] AppComponentsAPluginMayDraw =
    [
        "NMCarousel",
        "NMContainer",
        "NMGenreCard",
        "NMGrid",
        "NMGroupCard",
        "NMHomeCard",
        "NMSeasonCard",
        "NMSeasonTitle",
        "NMMusicCard",
        "NMMusicHomeCard",
        "NMTopResultCard",
        "NMTrackRow",
    ];

    [Fact]
    public void A_server_component_one_level_down_is_found()
    {
        PluginView view = PluginViews.Declarative(
            Node("grid", "NMGrid", Node("evil", NmPluginForbiddenComponents.ServerComponent))
        );

        PluginViewComponentGuard
            .FindForbidden(view)
            .Should()
            .Be(NmPluginForbiddenComponents.ServerComponent);
    }

    [Fact]
    public void A_server_component_three_levels_down_is_found()
    {
        PluginView view = PluginViews.Declarative(
            Node(
                "page",
                "NMCard",
                Node(
                    "list",
                    "NMContainer",
                    Node(
                        "grid",
                        "NMGrid",
                        Node("evil", NmPluginForbiddenComponents.ServerComponent)
                    )
                )
            )
        );

        PluginViewComponentGuard
            .FindForbidden(view)
            .Should()
            .Be(NmPluginForbiddenComponents.ServerComponent);
    }

    [Fact]
    public void A_server_component_written_by_hand_into_props_is_found()
    {
        PluginView view = PluginViews.Declarative(
            new PluginComponent
            {
                Id = "grid",
                Component = "NMGrid",
                Props = new()
                {
                    ["id"] = "grid",
                    ["items"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "evil",
                            ["component"] = "nmservercomponent",
                            ["props"] = new Dictionary<string, object?> { ["url"] = "x.js" },
                        },
                    },
                },
            }
        );

        PluginViewComponentGuard.FindForbidden(view).Should().Be("nmservercomponent");
    }

    [Fact]
    public void A_server_component_in_a_named_slot_is_found()
    {
        PluginView view = PluginViews.Declarative(
            new PluginComponent
            {
                Id = "card",
                Component = "NMCard",
                Props = new()
                {
                    ["slots"] = new Dictionary<string, object?>
                    {
                        ["footer"] = new[]
                        {
                            Node("evil", NmPluginForbiddenComponents.ServerComponent),
                        },
                    },
                },
            }
        );

        PluginViewComponentGuard.FindForbidden(view).Should().NotBeNull();
    }

    [Theory]
    [InlineData("NMServerComponent")]
    [InlineData("NMSERVERCOMPONENT")]
    [InlineData("nmServerComponent")]
    [InlineData(" NMServerComponent")]
    [InlineData("NMServerComponent ")]
    [InlineData("\tnmservercomponent\n")]
    [InlineData("\u00A0NMServerComponent\u00A0")]
    public void A_respelled_or_padded_server_component_is_found(string spelling)
    {
        NmPluginForbiddenComponents.IsForbidden(spelling).Should().BeTrue();

        PluginView view = PluginViews.Declarative(Node("grid", "NMGrid", Node("evil", spelling)));

        PluginViewComponentGuard.FindForbidden(view).Should().Be(spelling);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NMServer Component")]
    [InlineData("NMServerComponents")]
    public void A_name_that_is_not_the_server_component_is_not_forbidden(string? spelling)
    {
        NmPluginForbiddenComponents.IsForbidden(spelling).Should().BeFalse();
    }

    [Fact]
    public async Task An_append_is_refused_when_its_shelf_holds_a_server_component()
    {
        PluginUiController controller = Controller(
            PluginViews.Declarative(
                Node("heading", "NMContentHeader"),
                Node(
                    "all",
                    "NMGrid",
                    Node("card", "NMMusicCard"),
                    Node("evil", " NMServerComponent ")
                )
            ),
            "?route=/stations&append=all&cursor=36&limit=4"
        );

        IActionResult result = await controller.View(PluginId, "/stations", null, default);

        result
            .Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task An_append_is_refused_when_the_server_component_sits_outside_its_shelf()
    {
        // The whole view is checked before the append keeps only its shelf, so a
        // plugin cannot hide the component in the part that is not sent.
        PluginUiController controller = Controller(
            PluginViews.Declarative(
                Node("evil", NmPluginForbiddenComponents.ServerComponent),
                Node("all", "NMGrid", Node("card", "NMMusicCard"))
            ),
            "?route=/stations&append=all&limit=4"
        );

        IActionResult result = await controller.View(PluginId, "/stations", null, default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
    }

    [Fact]
    public void A_view_built_from_every_sdk_builder_passes()
    {
        PluginView view = PluginViews.Declarative(
            PluginViews.Container(
                "a",
                PluginViews.Text("b", "t", "title"),
                PluginViews.Image("c", "u"),
                PluginViews.Grid("f", PluginViews.Card("g", "t", "s", "img")),
                PluginViews.Detail("h", "t"),
                PluginViews.Button("i", "l", PluginActionIntent.RefreshView()),
                PluginViews.Form("k", "Go", PluginActionIntent.RefreshView()),
                PluginViews.EmptyState("l", "t"),
                PluginViews.Spinner("m"),
                PluginViews.Table("n", [new() { Key = "name", Label = "Name" }], []),
                PluginViews.Progress("o", 0.5),
                PluginViews.Badge("p", "l")
            )
        );

        PluginViewComponentGuard.FindForbidden(view).Should().BeNull();
    }

    [Fact]
    public void A_view_naming_every_app_component_a_plugin_may_draw_passes()
    {
        PluginView view = PluginViews.Declarative([
            .. AppComponentsAPluginMayDraw.Select(name => Node(name.ToLowerInvariant(), name)),
        ]);

        PluginViewComponentGuard.FindForbidden(view).Should().BeNull();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/details/one")]
    public async Task The_sample_plugin_views_pass(string route)
    {
        PluginView view = await new DashboardSamplePlugin().GetViewAsync(
            new() { Route = route, Caller = AnyCaller },
            CancellationToken.None
        );

        PluginViewComponentGuard.FindForbidden(view).Should().BeNull();
    }

    [Fact]
    public void Only_the_server_component_is_forbidden_today()
    {
        NmPluginForbiddenComponents.All.Should().BeEquivalentTo(["NMServerComponent"]);
        NmAppComponents.All.Where(NmPluginForbiddenComponents.IsForbidden).Should().BeEmpty();
    }

    [Fact]
    public async Task The_view_endpoint_refuses_a_view_with_a_nested_server_component()
    {
        PluginUiController controller = Controller(
            PluginViews.Declarative(
                Node("grid", "NMGrid", Node("evil", NmPluginForbiddenComponents.ServerComponent))
            )
        );

        IActionResult result = await controller.View(PluginId, "/", null, default);

        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        refused
            .Value.Should()
            .BeOfType<PluginRefusalDto>()
            .Which.Code.Should()
            .Be(PluginRefusalCodes.ViewComponentForbidden);
    }

    [Fact]
    public async Task The_view_endpoint_still_sends_a_normal_view()
    {
        PluginUiController controller = Controller(
            PluginViews.Declarative(
                Node("grid", "NMGrid", Node("card", "NMMusicCard")),
                Node("row", "NMCarousel", Node("card2", "NMMusicCard"))
            )
        );

        IActionResult result = await controller.Submit(PluginId, "/", null, null, default);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task The_view_endpoint_answers_an_append_with_its_one_shelf_held_to_the_limit()
    {
        PluginUiController controller = Controller(
            PluginViews.Declarative(
                Node("heading", "NMContentHeader"),
                Node(
                    "all",
                    "NMGrid",
                    [.. Enumerable.Range(0, 9).Select(index => Node($"c{index}", "NMMusicCard"))]
                )
            ),
            "?route=/stations&append=all&cursor=36&limit=4"
        );

        IActionResult result = await controller.View(PluginId, "/stations", null, default);

        PluginView sent = result
            .Should()
            .BeOfType<OkObjectResult>()
            .Subject.Value.Should()
            .BeOfType<NoMercy.Api.DTOs.Common.DataResponseDto<PluginView>>()
            .Subject.Data!;
        sent.Components.Should().ContainSingle().Which.Id.Should().Be("all");
        ((IEnumerable<PluginComponent>)sent.Components![0].Props["items"]!).Should().HaveCount(4);
    }

    private static PluginComponent Node(
        string id,
        string component,
        params PluginComponent[] items
    ) =>
        new()
        {
            Id = id,
            Component = component,
            Props = new() { ["id"] = id },
            Items = [.. items],
        };

    private static PluginUiController Controller(PluginView view, string query = "")
    {
        Mock<IUiPlugin> plugin = new();
        plugin
            .Setup(one =>
                one.GetViewAsync(It.IsAny<PluginViewRequest>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(view);
        plugin.SetupGet(one => one.Routes).Returns(new PluginRouteTable());

        Mock<IPluginManager> manager = new();
        manager
            .Setup(one => one.GetPluginInfo(PluginId))
            .Returns(
                new PluginInfo
                {
                    Id = PluginId,
                    Name = "Guarded UI",
                    Description = "A plugin with a view",
                    Version = new Version(1, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = new() { Hooks = ["ui"] },
                }
            );
        manager.Setup(one => one.GetPluginInstance(PluginId)).Returns(plugin.Object);

        Mock<IPluginAccessResolver> resolver = new();
        resolver.Setup(one => one.Resolve(PluginId, Owner)).Returns(PluginAccess.Owned);

        DefaultHttpContext http = new()
        {
            User = new(
                new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, Owner.ToString()),
                        new Claim(ClaimTypes.Role, "owner"),
                    ],
                    "test"
                )
            ),
        };
        http.Request.QueryString = new(query);

        return new(manager.Object, resolver.Object, NullLogger<PluginUiController>.Instance)
        {
            ControllerContext = new() { HttpContext = http },
        };
    }
}
