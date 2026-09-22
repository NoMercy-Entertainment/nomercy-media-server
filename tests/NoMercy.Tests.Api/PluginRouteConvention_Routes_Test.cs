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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Pins the absolute path a plugin controller ends up on. Nothing asserted it
/// before, so the convention could mount plugins unversioned while every client
/// posted to the versioned base, and the mismatch read as a 404 from the
/// capability filter instead of a missing route.
/// </summary>
[Trait("Category", "Routes")]
public class PluginRouteConvention_Routes_Test
{
    private static readonly Ulid Owner = Ulid.NewUlid();

    private class OwningCatalog : IPluginAssemblyCatalog
    {
        public Ulid? OwnerOf(Assembly assembly) => Owner;
    }

    private class ForeignCatalog : IPluginAssemblyCatalog
    {
        public Ulid? OwnerOf(Assembly assembly) => null;
    }

    private class SampleController : PluginControllerBase
    {
        [HttpPost("SaveSettings")]
        public IActionResult SaveSettings() => Ok();
    }

    private static ControllerModel ApplyTo(IPluginAssemblyCatalog catalog)
    {
        ControllerModel controller = new(typeof(SampleController).GetTypeInfo(), []);
        controller.Selectors.Add(
            new() { AttributeRouteModel = new(new RouteAttribute("SaveSettings")) }
        );

        ApplicationModel application = new();
        application.Controllers.Add(controller);

        new PluginRouteConvention(catalog).Apply(application);

        return controller;
    }

    [Fact]
    public void PluginController_Is_Mounted_On_The_Versioned_Api_Base()
    {
        ControllerModel controller = ApplyTo(new OwningCatalog());

        Assert.Equal(
            $"api/v{{version:apiVersion}}/plugins/{Owner}/SaveSettings",
            controller.Selectors[0].AttributeRouteModel?.Template
        );
    }

    [Fact]
    public void PluginId_Comes_From_The_Catalog_Not_The_Url()
    {
        ControllerModel controller = ApplyTo(new OwningCatalog());

        Assert.Equal(Owner.ToString(), controller.RouteValues["pluginId"]);
        Assert.DoesNotContain(
            "{pluginId}",
            controller.Selectors[0].AttributeRouteModel?.Template ?? string.Empty
        );
    }

    [Fact]
    public void NonPlugin_Assembly_Is_Left_Alone()
    {
        ControllerModel controller = ApplyTo(new ForeignCatalog());

        Assert.Equal("SaveSettings", controller.Selectors[0].AttributeRouteModel?.Template);
        Assert.False(controller.RouteValues.ContainsKey("pluginId"));
    }
}
