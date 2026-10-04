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
        // Liveness/readiness probes for container orchestration and load balancers.
        "GET Health [Health.GetLiveness]",
        "GET Health/detailed [Health.GetDetailed]",
        "GET Health/ready [Health.GetReadiness]",
        // Setup-time liveness probe a client calls before any login exists.
        "GET status [Setup.Status]",
        // Blocklist feed: the token is in the path and a wrong token answers 404.
        "GET security/blocklist/{token} [Blocklist.Feed]",
        // Image bytes loaded by <img> tags, which send no bearer header.
        "GET images/{type}/{path} [Image.Image]",
        // public today; under review
        "DELETE images/{type}/{path} [Image.DeleteCache]",
        // Worker routes: HmacValidationMiddleware signs /api/v1/worker/*.
        "GET api/v{version:apiVersion}/worker-source [WorkerSource.Stream]",
        "GET api/v{version:apiVersion}/worker/source [WorkerSource.Stream]",
        "POST api/v{version:apiVersion}/worker/execute-task [WorkerExecution.ExecuteTask]",
        "POST api/v{version:apiVersion}/worker/tasks [WorkerExecution.ExecuteTask]",
        // Worker progress: HMAC-exempt by spec; a spoofed progress bar is the worst case.
        "POST api/v{version:apiVersion}/dashboard/workers/{workerId}/tasks/{taskId}/progress [Workers.ReceiveProgress]",
        "POST api/v{version:apiVersion}/distribution/workers/{workerId}/tasks/{taskId}/progress [Workers.ReceiveProgress]",
        // Inbound webhook: gated inside by the intake token check.
        "POST api/v{version:apiVersion}/intake/webhook [IntakeWebhook.Webhook]",
        // LAN device credential: refuses any caller outside the private network.
        "GET api/v{version:apiVersion}/plugins/{id:ulid}/lan/{deviceId}/{credential} [PluginLan.Device]",
        // public today; under review
        "GET manage/activity [Management.GetActivity]",
        "GET manage/app/status [Management.GetAppStatus]",
        "GET manage/autostart [Management.GetAutoStart]",
        "GET manage/config [Management.GetConfig]",
        "GET manage/logs [Management.GetLogs]",
        "GET manage/logs/stream [Management.StreamLogs]",
        "GET manage/plugins [Management.GetPlugins]",
        "GET manage/queue [Management.GetQueueStatus]",
        "GET manage/resources [Management.GetResources]",
        "GET manage/status [Management.GetStatus]",
        "POST manage/app/start [Management.StartApp]",
        "POST manage/app/stop [Management.StopApp]",
        "POST manage/autostart [Management.SetAutoStart]",
        "POST manage/restart [Management.Restart]",
        "POST manage/stop [Management.Stop]",
        "POST manage/update [Management.DownloadUpdate]",
        "PUT manage/config [Management.UpdateConfig]",
    ];

    private static List<string> DescribeAnonymousEndpoints(EndpointDataSource dataSource)
    {
        List<string> lines = [];

        foreach (Endpoint endpoint in dataSource.Endpoints)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
                continue;

            bool allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            bool requiresAuthorization =
                endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;

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
