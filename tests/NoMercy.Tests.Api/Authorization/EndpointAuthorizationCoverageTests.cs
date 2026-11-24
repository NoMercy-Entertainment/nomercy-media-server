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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
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
        // Public key a TV checks a cast control token against; holds no secret.
        // CastTokenController.cs: [AllowAnonymous] on the action.
        "GET /api/v{version:apiVersion}/cast/keys",
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
        "POST /manage/loglevel",
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
        // A worker registering, heartbeating and unregistering itself. [HmacSignedOrOwner]
        // admits a request HmacValidationMiddleware verified, or else only the owner
        // (default + Owner policy); the /dashboard/ alias is never signature-checked,
        // so there it is owner-only as before (WorkersController.cs).
        "POST /api/v{version:apiVersion}/distribution/workers/register",
        "POST /api/v{version:apiVersion}/distribution/workers/{workerId}/heartbeat",
        "DELETE /api/v{version:apiVersion}/distribution/workers/{workerId}",
        "POST /api/v{version:apiVersion}/dashboard/workers/register",
        "POST /api/v{version:apiVersion}/dashboard/workers/{workerId}/heartbeat",
        "DELETE /api/v{version:apiVersion}/dashboard/workers/{workerId}",
        // Plugin LAN device credential check, answered to the local network only;
        // PluginLanController.cs refuses with 403 outside it or on a wrong credential.
        "GET /api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}",
        // A media link minted for a LAN device: answered to a direct LAN caller
        // only, and the ticket is bound to that device (PluginMediaController.cs).
        "GET /api/v{version:apiVersion}/plugins/{id:ulid}/lan-media/{ticket}",
        // The routes a plugin's manifest names for LAN devices: the same network
        // and credential check first, every other method refused the same way.
        "DELETE,GET,HEAD,OPTIONS,PATCH,POST,PUT /api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential}/{**path}",
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

    /// <summary>
    /// The policies that admit only the people who run the server: "Owner", and
    /// "Moderator" (the Manage permission, or the owner). See ServiceConfiguration.Auth.cs.
    /// </summary>
    private static readonly HashSet<string> AdminPolicies = new(StringComparer.Ordinal)
    {
        "Owner",
        "Moderator",
    };

    /// <summary>
    /// Admin-area endpoints that do not carry an admin policy, each with the reason.
    /// Key format as in <see cref="AllowList"/>. An entry that says "needs owner decision"
    /// is open to every signed-in user of this server today; closing it could break the
    /// web or KMP app, so it waits for the owner.
    /// </summary>
    private const string WorkerProgressReason =
        "Worker progress push, anonymous by design: a caller can overwrite a task's displayed "
        + "progress and evict entries from the 500-entry progress store; display only.";

    private static readonly Dictionary<string, string> AdminAreaAllowList = new(
        StringComparer.Ordinal
    )
    {
        // Every signed-in client of this server, by design.
        ["GET /api/v{version:apiVersion}/dashboard/configuration/languages"] =
            "MediaAccess: the language list for profile and playback settings.",
        ["GET /api/v{version:apiVersion}/dashboard/configuration/countries"] =
            "MediaAccess: the country list for profile and playback settings.",
        ["GET /api/v{version:apiVersion}/dashboard/server/info"] =
            "MediaAccess: server name, version and hardware; the web account page shows it.",
        ["GET /api/v{version:apiVersion}/dashboard/server/update/check"] =
            "Every signed-in user reads the last known update status; only a moderator's "
            + "call refreshes it, through UpdateChecker.RefreshAsync, at most once per 10 minutes.",
        ["POST /api/v{version:apiVersion}/dashboard/activity"] =
            "MediaAccess: a client reports its own activity.",
        ["POST /api/v{version:apiVersion}/dashboard/activity/playback-failure"] =
            "MediaAccess: a client reports its own playback failure.",
        ["GET /api/v{version:apiVersion}/dashboard/recommendations/movies"] =
            "MediaAccess: read-only TMDB suggestions; the diagnostics route stays Moderator.",
        ["GET /api/v{version:apiVersion}/dashboard/recommendations/tv"] =
            "MediaAccess: read-only TMDB suggestions; the diagnostics route stays Moderator.",
        ["GET /api/v{version:apiVersion}/dashboard/recommendations/anime"] =
            "MediaAccess: read-only TMDB suggestions; the diagnostics route stays Moderator.",
        ["GET /api/v{version:apiVersion}/dashboard/recommendations/{type}/{id:int}"] =
            "MediaAccess: read-only TMDB suggestions; the diagnostics route stays Moderator.",
        ["PATCH /api/v{version:apiVersion}/dashboard/users/notifications"] =
            "Self-service: imperative IsAllowed, touches only the caller, answers 501 today.",
        ["PATCH /api/v{version:apiVersion}/dashboard/users/{id:guid}/notifications"] =
            "Self-service: imperative IsAllowed, reads only the caller (not {id}), answers 501 today.",
        ["GET /api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/users/{userId:guid}/export"] =
            "Self-service: the caller's own plugin data, or the owner's; checked in the action.",
        ["DELETE /api/v{version:apiVersion}/dashboard/plugins/{id:ulid}/users/{userId:guid}"] =
            "Self-service: the caller's own plugin data, or the owner's; checked in the action.",
        // Read-only optical access, a permission a household member can hold without
        // full library access (OpticalAccessRequirement); tray, rip and confirm stay Moderator.
        ["GET /api/v{version:apiVersion}/dashboard/optical/drives"] = "OpticalAccess by design.",
        ["GET /api/v{version:apiVersion}/dashboard/optical/{drivePath}"] =
            "OpticalAccess by design.",
        ["GET /api/v{version:apiVersion}/dashboard/optical/{drivePath}/probe"] =
            "OpticalAccess by design.",
        ["GET /api/v{version:apiVersion}/dashboard/optical/{drivePath}/title/{titleIndex:int}"] =
            "OpticalAccess by design.",
        ["POST /api/v{version:apiVersion}/dashboard/optical/{drivePath}/resolve"] =
            "OpticalAccess by design: a read-only TMDB match preview.",
        ["POST /api/v{version:apiVersion}/dashboard/optical/{drivePath}/play/{playlistId}"] =
            "OpticalAccess by design: watching a disc live.",
        ["POST /api/v{version:apiVersion}/dashboard/optical/{drivePath}/stop"] =
            "OpticalAccess by design: any OpticalAccess user can stop another member's disc "
            + "session on that drive; no check ties the session to its starter.",
        // Plugin UI: PluginUiController sits in the dashboard folder but serves every user;
        // IPluginAccessResolver decides per plugin what each caller may see or use.
        ["GET /api/v{version:apiVersion}/plugins/ui"] = "Plugin UI, per-plugin access.",
        ["GET /api/v{version:apiVersion}/plugins/ui/browse"] = "Plugin UI, per-plugin access.",
        ["GET /api/v{version:apiVersion}/plugins/{id:ulid}/translations/{locale}"] =
            "Plugin UI, per-plugin access.",
        ["GET /api/v{version:apiVersion}/plugins/{id:ulid}/view"] = "Plugin UI, per-plugin access.",
        ["POST /api/v{version:apiVersion}/plugins/{id:ulid}/view"] =
            "Plugin UI, per-plugin access.",
        // Encode workers have no bearer token. [HmacSignedOrOwner] admits a request
        // HmacValidationMiddleware verified, or else only the owner; progress is
        // HMAC-exempt by design (see AllowList above).
        ["POST /api/v{version:apiVersion}/distribution/workers/register"] =
            "Worker: HMAC or owner.",
        ["POST /api/v{version:apiVersion}/distribution/workers/{workerId}/heartbeat"] =
            "Worker: HMAC or owner.",
        ["DELETE /api/v{version:apiVersion}/distribution/workers/{workerId}"] =
            "Worker: HMAC or owner.",
        [
            "POST /api/v{version:apiVersion}/distribution/workers/{workerId}/tasks/{taskId}/progress"
        ] = WorkerProgressReason,
        ["POST /api/v{version:apiVersion}/dashboard/workers/register"] = "Worker: owner only here.",
        ["POST /api/v{version:apiVersion}/dashboard/workers/{workerId}/heartbeat"] =
            "Worker: owner only here.",
        ["DELETE /api/v{version:apiVersion}/dashboard/workers/{workerId}"] =
            "Worker: owner only here.",
        ["POST /api/v{version:apiVersion}/dashboard/workers/{workerId}/tasks/{taskId}/progress"] =
            WorkerProgressReason,
    };

    /// <summary>
    /// The admin rule on top of <see cref="EveryEndpoint_RequiresAuth_UnlessOnTheAllowList"/>,
    /// which already holds every endpoint of the host, in any folder, to authentication.
    /// </summary>
    [Fact]
    public void EveryAdminAreaEndpoint_RequiresAnAdminPolicy_UnlessOnTheAdminAllowList()
    {
        List<RouteEndpoint> adminEndpoints = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(IsInAdminArea)
            .ToList();

        Assert.NotEmpty(adminEndpoints);

        Dictionary<string, bool> adminOnly = new(StringComparer.Ordinal);
        foreach (RouteEndpoint endpoint in adminEndpoints)
            adminOnly[Describe(endpoint)] = RequiresAdminPolicy(endpoint);

        List<string> notAdminOnly = adminOnly
            .Where(pair => !pair.Value && !AdminAreaAllowList.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        List<string> staleEntries = AdminAreaAllowList
            .Keys.Where(key => !adminOnly.TryGetValue(key, out bool isAdminOnly) || isAdminOnly)
            .Order(StringComparer.Ordinal)
            .ToList();

        output.WriteLine(
            $"{adminOnly.Count} admin-area endpoints: {adminOnly.Count(pair => pair.Value)} admin-only, "
                + $"{AdminAreaAllowList.Count} allow-listed"
        );

        Assert.True(
            notAdminOnly.Count == 0,
            "These admin-area endpoints admit a user without the Owner or Moderator policy and "
                + "are not on the admin allow-list:\n"
                + string.Join("\n", notAdminOnly)
        );
        Assert.True(
            staleEntries.Count == 0,
            "These admin allow-list entries no longer match a non-admin endpoint; remove them:\n"
                + string.Join("\n", staleEntries)
        );
    }

    /// <summary>
    /// The admin area is every controller under Controllers/V1/Dashboard and
    /// Controllers/V1/Encoder (the encoder settings), wherever it is routed, and every
    /// route under /dashboard/, whichever controller serves it.
    /// </summary>
    private static bool IsInAdminArea(RouteEndpoint endpoint)
    {
        string? controllerNamespace = endpoint
            .Metadata.GetMetadata<ControllerActionDescriptor>()
            ?.ControllerTypeInfo.Namespace;
        if (
            controllerNamespace is not null
            && (
                controllerNamespace.StartsWith(
                    "NoMercy.Api.Controllers.V1.Dashboard",
                    StringComparison.Ordinal
                )
                || controllerNamespace.StartsWith(
                    "NoMercy.Api.Controllers.V1.Encoder",
                    StringComparison.Ordinal
                )
            )
        )
            return true;

        return endpoint.RoutePattern.RawText?.Contains("/dashboard/", StringComparison.Ordinal)
            ?? false;
    }

    /// <summary>
    /// Every [Authorize] on the class and the action must pass, so one admin policy among
    /// them makes the endpoint admin-only, unless [AllowAnonymous] skips them all.
    /// </summary>
    /// <summary>The hubs only the people who run the server need (ApplicationConfiguration.cs).</summary>
    private static readonly HashSet<string> AdminHubPaths = new(StringComparer.Ordinal)
    {
        "/dashboardHub",
        "/ripperHub",
        "/drivesHub",
        "/contentAnalysisHub",
    };

    /// <summary>
    /// Admin hub methods a client can call without an admin policy on the method or hub,
    /// each with the reason. Key: "DeclaringType.Method". [Authorize] on a hub method
    /// refuses with a HubException ("Failed to invoke ... because user is unauthorized")
    /// before HubCommandResultFilter runs, where these methods refuse with a forbidden
    /// HubCommandResult the clients handle; moving them would change that reply.
    /// AdminHubNonModeratorRefusalTests proves each one refuses a non-moderator.
    /// </summary>
    private static readonly Dictionary<string, string> AdminHubAllowList = new(
        StringComparer.Ordinal
    )
    {
        ["DashboardHub.StartResources"] =
            "Imperative IsModerator, forbidden result; the web calls it without a catch.",
        ["DashboardHub.StopResources"] = "Imperative IsModerator, forbidden result.",
        ["RipperHub.GetDriveState"] = "Imperative IsModerator, forbidden result.",
        ["ConnectionHub.Devices"] = "Every hub: the caller's own connected devices, no admin data.",
        ["ConnectionHub.GetCountryFromContext"] =
            "Every hub: echoes the caller's own request country.",
        ["ConnectionHub.GetLanguageFromContext"] =
            "Every hub: echoes the caller's own request language.",
    };

    [Fact]
    public void EveryAdminHubMethod_RequiresAnAdminPolicy_UnlessOnTheAdminHubAllowList()
    {
        List<Type> adminHubs = factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => AdminHubPaths.Contains(endpoint.RoutePattern.RawText ?? ""))
            .Select(endpoint => endpoint.Metadata.GetMetadata<HubMetadata>()?.HubType)
            .OfType<Type>()
            .Distinct()
            .ToList();

        Assert.Equal(AdminHubPaths.Count, adminHubs.Count);

        Dictionary<string, bool> adminOnly = new(StringComparer.Ordinal);
        foreach (Type hub in adminHubs)
        foreach (MethodInfo method in HubMethods(hub))
            adminOnly[$"{method.DeclaringType!.Name}.{method.Name}"] = RequiresAdminPolicy(
                hub,
                method
            );

        List<string> notAdminOnly = adminOnly
            .Where(pair => !pair.Value && !AdminHubAllowList.ContainsKey(pair.Key))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        List<string> staleEntries = AdminHubAllowList
            .Keys.Where(key => !adminOnly.TryGetValue(key, out bool isAdminOnly) || isAdminOnly)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            notAdminOnly.Count == 0,
            "These admin hub methods admit a caller without the Owner or Moderator policy and "
                + "are not on the admin hub allow-list:\n"
                + string.Join("\n", notAdminOnly)
        );
        Assert.True(
            staleEntries.Count == 0,
            "These admin hub allow-list entries no longer match a non-admin hub method; "
                + "remove them:\n"
                + string.Join("\n", staleEntries)
        );
    }

    /// <summary>
    /// The methods SignalR lets a client invoke: public instance methods, without those
    /// of object, Hub and IDisposable, and without property accessors.
    /// </summary>
    private static IEnumerable<MethodInfo> HubMethods(Type hub)
    {
        MethodInfo[] disposal = hub.GetInterfaceMap(typeof(IDisposable)).TargetMethods;

        return hub.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName && !disposal.Contains(method))
            .Where(method =>
            {
                Type baseDeclaringType = method.GetBaseDefinition().DeclaringType!;
                return baseDeclaringType != typeof(object) && baseDeclaringType != typeof(Hub);
            });
    }

    private static bool RequiresAdminPolicy(Type hub, MethodInfo method) =>
        method
            .GetCustomAttributes<AuthorizeAttribute>(true)
            .Concat(hub.GetCustomAttributes<AuthorizeAttribute>(true))
            .Any(data => data.Policy is not null && AdminPolicies.Contains(data.Policy));

    private static bool RequiresAdminPolicy(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            return false;

        return endpoint
            .Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(data => data.Policy is not null && AdminPolicies.Contains(data.Policy));
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
