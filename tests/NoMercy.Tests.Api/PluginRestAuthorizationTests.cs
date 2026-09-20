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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Api.Plugins;
using NoMercy.Plugins.Mvc;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// No <c>[Authorize]</c> was imposed on a plugin's REST routes, so whether a
/// plugin endpoint asked for a token was the plugin author's decision. Every
/// plugin route now requires one, and a plugin that wants an open endpoint says
/// so in its manifest where the owner can read it — not in code nobody reads.
/// </summary>
[Trait("Category", "Authorization")]
public class PluginRestAuthorizationTests
{
    private static readonly Ulid Owner = Ulid.NewUlid();

    private class Catalog(bool anonymous) : IPluginAssemblyCatalog
    {
        public Ulid? OwnerOf(Assembly assembly) => Owner;

        public bool AllowsAnonymousRest(Assembly assembly) => anonymous;
    }

    private class ForeignCatalog : IPluginAssemblyCatalog
    {
        public Ulid? OwnerOf(Assembly assembly) => null;
    }

    private class SampleController : PluginControllerBase
    {
        [HttpGet("Settings")]
        public IActionResult Settings() => Ok();
    }

    /// <summary>A plugin that tried to open its own endpoint in code.</summary>
    [AllowAnonymous]
    private class OptOutController : PluginControllerBase
    {
        [HttpGet("Open")]
        public IActionResult Open() => Ok();
    }

    private static ControllerModel ApplyTo(
        IPluginAssemblyCatalog catalog,
        Type controllerType,
        string template
    )
    {
        ControllerModel controller = new(
            controllerType.GetTypeInfo(),
            GetAttributes(controllerType)
        );
        controller.Selectors.Add(new() { AttributeRouteModel = new(new RouteAttribute(template)) });

        foreach (object attribute in GetAttributes(controllerType))
        {
            if (attribute is IFilterMetadata filter)
                controller.Filters.Add(filter);
        }

        ApplicationModel application = new();
        application.Controllers.Add(controller);

        new PluginRouteConvention(catalog).Apply(application);

        return controller;
    }

    private static List<object> GetAttributes(Type controllerType) =>
        [.. controllerType.GetCustomAttributes(inherit: true)];

    private static bool RequiresAuthorization(ControllerModel controller) =>
        controller.Selectors[0].EndpointMetadata.OfType<IAuthorizeData>().Any()
        && !controller.Selectors[0].EndpointMetadata.OfType<IAllowAnonymous>().Any()
        && !controller.Filters.OfType<IAllowAnonymous>().Any();

    [Fact]
    public void A_plugin_route_requires_a_token_by_default()
    {
        ControllerModel controller = ApplyTo(
            new Catalog(anonymous: false),
            typeof(SampleController),
            "Settings"
        );

        RequiresAuthorization(controller).Should().BeTrue();
    }

    [Fact]
    public void A_manifest_that_asks_for_an_open_endpoint_gets_one()
    {
        ControllerModel controller = ApplyTo(
            new Catalog(anonymous: true),
            typeof(SampleController),
            "Settings"
        );

        controller.Selectors[0].EndpointMetadata.OfType<IAllowAnonymous>().Should().NotBeEmpty();
    }

    /// <summary>
    /// The manifest is the only way out. A plugin that writes
    /// <c>[AllowAnonymous]</c> on its own controller is opting out of a decision
    /// that belongs to the owner, so the convention takes it back off.
    /// </summary>
    [Fact]
    public void A_plugin_cannot_open_its_own_endpoint_in_code()
    {
        ControllerModel controller = ApplyTo(
            new Catalog(anonymous: false),
            typeof(OptOutController),
            "Open"
        );

        RequiresAuthorization(controller).Should().BeTrue();
    }

    [Fact]
    public void A_controller_that_is_not_a_plugins_is_left_alone()
    {
        ControllerModel controller = ApplyTo(
            new ForeignCatalog(),
            typeof(SampleController),
            "Settings"
        );

        controller.Selectors[0].EndpointMetadata.OfType<IAuthorizeData>().Should().BeEmpty();
    }
}
