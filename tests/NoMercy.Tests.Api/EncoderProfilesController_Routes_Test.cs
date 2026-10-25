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
using NoMercy.Api.Controllers.V1.Dashboard.Encoder;
using NoMercy.Api.Controllers.V1.Encoder;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Static reflection tests — no DI, no HTTP, no database.
///
/// Purpose: guarantee the expected route set exists on both controllers and
/// that every legacy endpoint carries [Obsolete]. These tests catch future
/// regressions where someone adds a new endpoint without registering it on
/// both sides or forgets the Obsolete marker.
/// </summary>
[Trait("Category", "Routes")]
public partial class EncoderProfilesController_Routes_Test
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static IEnumerable<MethodInfo> PublicActions(Type controller) =>
        controller
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    /// <summary>
    /// Finds an action method that carries the given HTTP verb attribute whose
    /// Template matches <paramref name="routeSuffix"/> (empty string for
    /// parameterless verbs like GET /).
    /// </summary>
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

            // Null template means the route is the controller base (index).
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

    /// <summary>
    /// Drops route-parameter constraints so the route SET is compared by verb +
    /// path structure, not by constraint syntax. "{id:ulid}" → "{id}" — the
    /// endpoint still exists at /{id}; whether it's ulid-constrained is a routing
    /// detail this test doesn't guard.
    /// </summary>
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

    private static void AssertEndpointObsolete(
        Type controller,
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        MethodInfo? method = FindAction(controller, httpVerb, routeSuffix);
        Assert.True(
            method is not null,
            $"Endpoint not found on {controller.Name}: {description} — cannot check [Obsolete]"
        );

        ObsoleteAttribute? obsolete = method!.GetCustomAttribute<ObsoleteAttribute>();
        Assert.True(
            obsolete is not null,
            $"Endpoint {controller.Name}.{method.Name} ({description}) is missing [Obsolete]"
        );
    }

    // =========================================================================
    // EncoderProfilesController — expected full CRUD surface
    // =========================================================================

    [Theory]
    [InlineData(typeof(HttpGetAttribute), "", "GET / (index)")]
    [InlineData(typeof(HttpGetAttribute), "{id}", "GET /{id}")]
    [InlineData(typeof(HttpPostAttribute), "", "POST / (create)")]
    [InlineData(typeof(HttpGetAttribute), "tags", "GET /tags")]
    [InlineData(typeof(HttpGetAttribute), "{id}/resolve", "GET /{id}/resolve")]
    [InlineData(typeof(HttpDeleteAttribute), "{id}", "DELETE /{id}")]
    [InlineData(typeof(HttpPostAttribute), "validate", "POST /validate")]
    [InlineData(typeof(HttpPostAttribute), "{id}/preview", "POST /{id}/preview")]
    [InlineData(typeof(HttpPutAttribute), "{id}", "PUT /{id}")]
    [InlineData(typeof(HttpPostAttribute), "{parentId}/clone", "POST /{parentId}/clone")]
    [InlineData(typeof(HttpPostAttribute), "import", "POST /import")]
    [InlineData(typeof(HttpGetAttribute), "{id}/export", "GET /{id}/export")]
    public void EncoderProfilesController_HasExpectedEndpoint(
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        AssertEndpointExists(typeof(EncoderProfilesController), httpVerb, routeSuffix, description);
    }

    // =========================================================================
    // EncodingPresetsController — every action must carry [Obsolete]
    // =========================================================================

    [Theory]
    [InlineData(typeof(HttpGetAttribute), "", "GET / (list)")]
    [InlineData(typeof(HttpGetAttribute), "{id}", "GET /{id}")]
    [InlineData(typeof(HttpPostAttribute), "", "POST / (create)")]
    [InlineData(typeof(HttpPutAttribute), "{id}", "PUT /{id}")]
    [InlineData(typeof(HttpGetAttribute), "tags", "GET /tags")]
    [InlineData(typeof(HttpPostAttribute), "{id}/clone", "POST /{id}/clone")]
    [InlineData(typeof(HttpGetAttribute), "{id}/resolve", "GET /{id}/resolve")]
    [InlineData(typeof(HttpGetAttribute), "{id}/export", "GET /{id}/export")]
    [InlineData(typeof(HttpPostAttribute), "import", "POST /import")]
    [InlineData(typeof(HttpPostAttribute), "import-url", "POST /import-url")]
    [InlineData(typeof(HttpPostAttribute), "validate", "POST /validate")]
    [InlineData(typeof(HttpPostAttribute), "preview", "POST /preview")]
    [InlineData(typeof(HttpDeleteAttribute), "{id}", "DELETE /{id}")]
    public void EncodingPresetsController_EndpointIsObsolete(
        Type httpVerb,
        string routeSuffix,
        string description
    )
    {
        AssertEndpointObsolete(
            typeof(EncodingPresetsController),
            httpVerb,
            routeSuffix,
            description
        );
    }
}
