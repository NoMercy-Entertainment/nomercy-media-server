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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

/// <summary>
/// Ratchet: every routed endpoint of the built app must require authentication
/// unless it is on the short allow-list below. The list is read from what the
/// app exposes anonymously today, each entry with the reason it is public.
/// </summary>
/// <remarks>
/// <para>
/// The data source is the real <see cref="EndpointDataSource"/> of the test host,
/// so a new controller action, hub or route is caught the moment it is mapped,
/// without scanning source files. An endpoint is anonymous when it carries
/// <see cref="IAllowAnonymous"/> metadata, or when it carries no
/// <see cref="IAuthorizeData"/> at all: the authorization setup registers a
/// default policy but no fallback policy, so an endpoint without an attribute
/// is reachable by anyone.
/// </para>
/// <para>
/// Routes that no endpoint owns (static file branches such as /transcodes and
/// the dynamic static-files middleware) are not in the data source and are not
/// covered here; they keep their own tests.
/// </para>
/// </remarks>
[Trait("Category", "Contract")]
public class AnonymousEndpointAllowListTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public AnonymousEndpointAllowListTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    // One line per entry: why the endpoint is public. An entry that reads
    // "public today; under review" is listed only so the ratchet stays honest;
    // it is not an endorsement.
    private static readonly string[] AnonymousAllowList =
    [
    ];

    private static List<string> DescribeAnonymousEndpoints(EndpointDataSource dataSource)
    {
        List<string> lines = [];

        foreach (Endpoint endpoint in dataSource.Endpoints)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
                continue;

            bool allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            bool requiresAuthorization = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;

            if (!allowsAnonymous && requiresAuthorization)
                continue;

            ControllerActionDescriptor? actionDescriptor =
                endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
            string owner = actionDescriptor is null
                ? "(hub/other)"
                : $"{actionDescriptor.ControllerName}.{actionDescriptor.ActionName}";

            HttpMethodMetadata? methodMetadata =
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
            string methods = methodMetadata is null
                ? "(any)"
                : string.Join("|", methodMetadata.HttpMethods);

            lines.Add($"{methods} {routeEndpoint.RoutePattern.RawText} [{owner}]");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Fact]
    public void EveryEndpoint_RequiresAuth_UnlessOnTheAnonymousAllowList()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        EndpointDataSource dataSource =
            scope.ServiceProvider.GetRequiredService<EndpointDataSource>();

        List<string> anonymous = DescribeAnonymousEndpoints(dataSource);
        List<string> allowed = AnonymousAllowList.OrderBy(r => r, StringComparer.Ordinal).ToList();

        List<string> notAllowed = anonymous.Except(allowed, StringComparer.Ordinal).ToList();
        List<string> stale = allowed.Except(anonymous, StringComparer.Ordinal).ToList();

        Assert.True(
            notAllowed.Count == 0,
            "Endpoint(s) reachable without authentication and not on the allow-list. "
                + "Add [Authorize], or add the entry with one line on why it is public: "
                + string.Join(", ", notAllowed)
        );
        Assert.True(
            stale.Count == 0,
            "Allow-list entries that no longer match an anonymous endpoint; remove them so the list stays short: "
                + string.Join(", ", stale)
        );
    }
}
