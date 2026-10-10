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
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace NoMercy.Tests.Api.Authorization;

/// <summary>
/// Every routed endpoint of the real host (controllers and SignalR hubs, read from
/// <see cref="EndpointDataSource"/>) must require authentication, unless it is on the
/// short allow-list below. The server registers a default policy but no fallback
/// policy (ServiceConfiguration.Auth.cs), so an action with neither
/// <c>[Authorize]</c> nor <c>[AllowAnonymous]</c> is let through by UseAuthorization
/// and only AccessLogMiddleware stands between it and an anonymous caller. Such an
/// endpoint is reported as "unprotected" here, the same as one that is
/// deliberately anonymous, so that a new one cannot land unnoticed.
/// The list also fails when an entry goes stale, so it stays short and true.
/// </summary>
[Trait("Category", "Authorization")]
public sealed class EndpointAuthorizationCoverageTests(
    NoMercyApiFactory factory,
    ITestOutputHelper output
) : IClassFixture<NoMercyApiFactory>
{
    /// <summary>
    /// Endpoints that are reachable without a bearer token by design. Key format:
    /// "VERB route-template" exactly as <see cref="EndpointAuthorizationCoverageTests.Describe"/> writes it.
    /// Each entry says why it is anonymous and what still confines it.
    /// </summary>
    private static readonly HashSet<string> AllowList = new(StringComparer.Ordinal)
    {
        // Liveness/readiness probes for service managers and load balancers.
        // HealthController.cs: [AllowAnonymous] on the class.
        "GET /Health",
        "GET /Health/detailed",
        "GET /Health/ready",
        // Setup-state probe the apps poll before they have a token.
        // SetupController.cs: [AllowAnonymous] on the action, 30 s response cache.
        "GET /status",
        // Local management plane for the launcher/CLI. ManagementController.cs:
        // [AllowAnonymous] + [LocalhostOnly] on the class, so only loopback reaches it.
        "GET /manage/activity",
        "GET /manage/app/status",
        "GET /manage/autostart",
        "GET /manage/config",
        "GET /manage/logs",
        "GET /manage/logs/stream",
        "GET /manage/plugins",
        "GET /manage/queue",
        "GET /manage/resources",
        "GET /manage/status",
        "POST /manage/app/start",
        "POST /manage/app/stop",
        "POST /manage/autostart",
        "POST /manage/restart",
        "POST /manage/stop",
        "POST /manage/update",
        "PUT /manage/config",
        // Firewall blocklist feed, pulled on a schedule with the path token as its
        // only credential. BlocklistController.cs: IBlocklistFeedSettings.VerifyAsync
        // returns 404 for a wrong token.
        "GET /security/blocklist/{token}",
        // Inbound intake webhook from outside systems; authenticated by HMAC
        // signature (HmacValidationMiddleware, ProtectedPrefixes), not by a bearer.
        "POST /api/v{version:apiVersion}/intake/webhook",
        // Headless encode workers; authenticated by HMAC signature or
        // X-NoMercy-WorkerToken introspection in HmacValidationMiddleware.
        "POST /api/v{version:apiVersion}/worker/execute-task",
        "POST /api/v{version:apiVersion}/worker/tasks",
        // Worker source download; signed URL (ts + sig query) checked in the action.
        "GET /api/v{version:apiVersion}/worker/source",
        "GET /api/v{version:apiVersion}/worker-source",
        // Worker progress push; exempt from HMAC by design (HmacValidationMiddleware
        // ExemptSuffixes), the payload carries no secrets (WorkersController.cs).
        "POST /api/v{version:apiVersion}/dashboard/workers/{workerId}/tasks/{taskId}/progress",
        "POST /api/v{version:apiVersion}/distribution/workers/{workerId}/tasks/{taskId}/progress",
        // Plugin LAN device credential check, answered to the local network only;
        // PluginLanController.cs refuses with 403 outside it or on a wrong credential.
        "GET /api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}",
        // Artwork is public on purpose: clients load it through img tags and CSS
        // backgrounds, which carry no Authorization header, and the response sets
        // Access-Control-Allow-Origin: *. AccessLogMiddleware lists /images among
        // its ignored routes, so nothing gates it. DELETE needs MediaAccess (#478).
        "GET /images/{type}/{path}",
    };

    private enum Protection
    {
        Authorized,
        Anonymous,
        Unprotected,
    }

    [Fact]
    public async Task EveryEndpoint_RequiresAuth_UnlessOnTheAllowList()
    {
        IAuthorizationPolicyProvider policyProvider =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        AuthorizationPolicy? fallbackPolicy = await policyProvider.GetFallbackPolicyAsync();

        List<RouteEndpoint> endpoints = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .ToList();

        Assert.NotEmpty(endpoints);

        Dictionary<string, Protection> classified = new(StringComparer.Ordinal);
        foreach (RouteEndpoint endpoint in endpoints)
            classified[Describe(endpoint)] = Classify(endpoint, fallbackPolicy);

        List<string> openEndpoints = classified
            .Where(pair => pair.Value is not Protection.Authorized)
            .Select(pair => $"{pair.Key}  [{pair.Value}]")
            .Order(StringComparer.Ordinal)
            .ToList();

        List<string> notOnList = classified
            .Where(pair => pair.Value is not Protection.Authorized && !AllowList.Contains(pair.Key))
            .Select(pair => $"{pair.Key}  [{pair.Value}]")
            .Order(StringComparer.Ordinal)
            .ToList();

        List<string> staleEntries = AllowList
            .Where(key =>
                !classified.TryGetValue(key, out Protection value) || value is Protection.Authorized
            )
            .Order(StringComparer.Ordinal)
            .ToList();

        output.WriteLine(
            $"{classified.Count} endpoints: {classified.Count - openEndpoints.Count} authorized, "
                + $"{openEndpoints.Count} open (fallback policy: {(fallbackPolicy is null ? "none" : "set")})"
        );
        foreach (string line in openEndpoints)
            output.WriteLine(line);

        Assert.True(
            notOnList.Count == 0,
            "These endpoints accept anonymous callers and are not on the allow-list:\n"
                + string.Join("\n", notOnList)
        );
        Assert.True(
            staleEntries.Count == 0,
            "These allow-list entries no longer match an open endpoint; remove them:\n"
                + string.Join("\n", staleEntries)
        );
    }

    private static Protection Classify(RouteEndpoint endpoint, AuthorizationPolicy? fallbackPolicy)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return Protection.Anonymous;

        if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0)
            return Protection.Authorized;

        return fallbackPolicy is null ? Protection.Unprotected : Protection.Authorized;
    }

    private static string Describe(RouteEndpoint endpoint)
    {
        IReadOnlyList<string> methods =
            endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
        string verb =
            methods.Count == 0 ? "ANY" : string.Join(",", methods.Order(StringComparer.Ordinal));

        return $"{verb} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";
    }
}
