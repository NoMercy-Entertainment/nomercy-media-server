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
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NoMercy.Api.Controllers.V1.Media;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Reflection-only tests for <see cref="AnimeThemesController"/>,
/// <see cref="AnimeDemographicsController"/>, and <see cref="AnimeSeasonsController"/>.
///
/// This project has no WebApplicationFactory/auth-mock harness for
/// instantiating a controller directly with a mocked repository and a fake
/// authenticated HttpContext (see InboxController_Routes_Test.cs and
/// EncoderProfilesController_Routes_Test.cs, the two most structurally
/// similar existing controller tests) — new controller tests in this
/// project follow the same reflection-only route-surface pattern rather
/// than inventing a new harness.
/// </summary>
[Trait("Category", "Routes")]
public partial class AnimeThemesControllerTests
{
    // -------------------------------------------------------------------------
    // Helpers — mirror EncoderProfilesController_Routes_Test
    // -------------------------------------------------------------------------

    private static IEnumerable<MethodInfo> PublicActions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any());

    private static MethodInfo? FindAction(
        Type controller,
        Type httpVerbAttribute,
        string routeSuffix
    )
    {
        foreach (MethodInfo method in PublicActions(controller))
        {
            HttpMethodAttribute? attr = method
                .GetCustomAttributes(httpVerbAttribute, inherit: false)
                .Cast<HttpMethodAttribute>()
                .FirstOrDefault();

            if (attr is null)
                continue;

            string template = attr.Template ?? string.Empty;

            if (
                string.Equals(
                    StripConstraints(template),
                    StripConstraints(routeSuffix),
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return method;
        }

        return null;
    }

    private static string StripConstraints(string template) =>
        RouteConstraint().Replace(template, "{$1}");

    [GeneratedRegex(@"\{(\w+):[^}]+\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RouteConstraint();

    private static void AssertEndpointExists(
        Type controller,
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        MethodInfo? method = FindAction(controller, httpVerb, routeSuffix);
        Assert.True(
            method is not null,
            $"Missing endpoint on {controller.Name}: {description} (route suffix: \"{routeSuffix}\")"
        );
    }

    // -------------------------------------------------------------------------
    // Route surface assertions
    // -------------------------------------------------------------------------

    [Fact]
    public void AnimeThemesController_HasThemesEndpoint()
    {
        AssertEndpointExists(
            typeof(AnimeThemesController),
            typeof(HttpGetAttribute),
            "",
            "GET / (themes list)"
        );
    }

    [Fact]
    public void AnimeDemographicsController_HasDemographicsEndpoint()
    {
        AssertEndpointExists(
            typeof(AnimeDemographicsController),
            typeof(HttpGetAttribute),
            "",
            "GET / (demographics list)"
        );
    }

    [Fact]
    public void AnimeSeasonsController_HasSeasonsEndpoint()
    {
        AssertEndpointExists(
            typeof(AnimeSeasonsController),
            typeof(HttpGetAttribute),
            "",
            "GET / (seasons list)"
        );
    }

    [Theory]
    [InlineData(typeof(AnimeThemesController), "api/v{version:apiVersion}/anime/themes")]
    [InlineData(
        typeof(AnimeDemographicsController),
        "api/v{version:apiVersion}/anime/demographics"
    )]
    [InlineData(typeof(AnimeSeasonsController), "api/v{version:apiVersion}/anime/seasons")]
    public void Controller_HasExpectedRouteTemplate(Type controller, string expectedRoute)
    {
        RouteAttribute? route = controller.GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(route);
        Assert.Equal(expectedRoute, route!.Template);
    }
}
