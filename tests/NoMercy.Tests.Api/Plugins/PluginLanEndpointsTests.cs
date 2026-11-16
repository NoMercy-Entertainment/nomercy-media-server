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

using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.Api.Plugins;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// Which action a LAN route can reach: only a GET action of the plugin's own
/// controllers, under the plugin's own prefix. Never another plugin's
/// controller, never one of the server's, and never a plugin action whose
/// template escaped the prefix with <c>~/</c>.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLanEndpointsTests
{
    private const string Route = "hdhr/lineup.json";

    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y00000000000000F1");
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y00000000000000F2");

    private static readonly Assembly PluginAssembly = typeof(LanSamplePluginController).Assembly;

    [Fact]
    public void The_plugins_own_get_action_under_its_own_prefix_is_found()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}")
        );

        endpoints.Find(LiveTv, [Route], Route).Should().NotBeNull();
    }

    [Fact]
    public void Another_plugins_controller_is_never_reached()
    {
        // The template names this plugin's id, but the controller's assembly
        // belongs to another plugin: the assembly decides.
        PluginLanEndpoints endpoints = Endpoints(
            owner: Radio,
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}")
        );

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Fact]
    public void Another_plugins_prefix_is_never_reached()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action($"api/v{{version:apiVersion}}/plugins/{Radio}/{Route}")
        );

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Fact]
    public void A_server_controller_is_never_reached_even_under_the_plugins_prefix()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action(
                $"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}",
                controller: typeof(PluginLanController)
            )
        );

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Theory]
    [InlineData("media/{ticket}", "media/abc")]
    [InlineData("lan-media/{ticket}", "lan-media/abc")]
    public void The_servers_own_plugin_routes_are_never_reached(string route, string path)
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action(
                $"api/v{{version:apiVersion}}/plugins/{{id:ulid}}/{route}",
                controller: typeof(PluginMediaController)
            )
        );

        endpoints.Find(LiveTv, [route], path).Should().BeNull();
    }

    /// <summary>
    /// An action written with <c>~/</c> or <c>/</c> keeps its own template
    /// and loses the plugin's prefix. Without the prefix it is not a LAN route,
    /// even when the manifest names its template.
    /// </summary>
    [Theory]
    [InlineData("hdhr/lineup.json")]
    [InlineData("api/v{version:apiVersion}/hdhr/lineup.json")]
    public void A_plugin_action_that_escaped_its_prefix_is_never_reached(string template)
    {
        PluginLanEndpoints endpoints = Endpoints(owner: LiveTv, Action(template));

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Fact]
    public void An_action_that_is_not_get_is_never_reached()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}", methods: ["POST"]),
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}", anyMethod: true)
        );

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Fact]
    public void Two_get_actions_for_one_route_reach_neither()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}"),
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/{Route}")
        );

        endpoints.Find(LiveTv, [Route], Route).Should().BeNull();
    }

    [Fact]
    public void A_path_the_manifest_does_not_name_is_never_reached()
    {
        PluginLanEndpoints endpoints = Endpoints(
            owner: LiveTv,
            Action($"api/v{{version:apiVersion}}/plugins/{LiveTv}/hdhr/secret.json")
        );

        endpoints.Find(LiveTv, [Route], "hdhr/secret.json").Should().BeNull();
    }

    private static PluginLanEndpoints Endpoints(Ulid owner, params Endpoint[] endpoints) =>
        new(new DefaultEndpointDataSource(endpoints), new OwnedBy(owner));

    /// <summary>
    /// An action endpoint as MVC builds one. <paramref name="methods" /> null
    /// means an action that names no method and so answers every one.
    /// </summary>
    private static RouteEndpoint Action(
        string template,
        Type? controller = null,
        string[]? methods = null,
        bool anyMethod = false
    )
    {
        List<object> metadata =
        [
            new ControllerActionDescriptor
            {
                ControllerTypeInfo = (
                    controller ?? typeof(LanSamplePluginController)
                ).GetTypeInfo(),
            },
        ];

        if (!anyMethod)
            metadata.Add(new HttpMethodMetadata(methods ?? ["GET"]));

        return new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(template),
            0,
            new EndpointMetadataCollection(metadata),
            template
        );
    }

    /// <summary>
    /// Claims the test assembly, and the server's own too: a catalog that got
    /// that wrong must still not open a server controller, because only a
    /// plugin controller type is ever a candidate.
    /// </summary>
    private sealed class OwnedBy(Ulid owner) : IPluginAssemblyCatalog
    {
        public Ulid? OwnerOf(Assembly assembly) =>
            ReferenceEquals(assembly, PluginAssembly)
            || ReferenceEquals(assembly, typeof(PluginLanController).Assembly)
                ? owner
                : null;

        public bool AllowsAnonymousRest(Assembly assembly) => false;
    }
}
