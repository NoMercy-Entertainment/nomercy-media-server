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

using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.NmSystem.Logging;

namespace NoMercy.Api.Hubs.Filters;

/// <summary>
/// Logs every call, connect and disconnect on the four session hubs (device, music, video,
/// cast): who asked, what, and the real result. A new hub method is covered without new code.
/// </summary>
public sealed class SessionRequestLogFilter(
    ILogger<SessionRequestLogFilter> logger,
    SessionLogPolicy policy
) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        if (!IsSessionHub(invocationContext.Hub))
            return await next(invocationContext);

        string hub = invocationContext.Hub.GetType().Name;
        string method = invocationContext.HubMethodName;
        string user = invocationContext.Context.User.UserId().ToString();
        string device = SessionLogText.DeviceOf(invocationContext.Context);
        string connection = invocationContext.Context.ConnectionId;
        LogLevel level = policy.RequestLevel(method);

        logger.Log(
            level,
            "Hub request {Hub}.{Method} user={User} device={Device} connection={Connection} args=[{Arguments}]",
            hub,
            method,
            user,
            device,
            connection,
            HubArgumentSummary.Describe(
                invocationContext.HubMethod,
                invocationContext.HubMethodArguments
            )
        );

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            object? result = await next(invocationContext);

            if (result is HubCommandResult { Ok: false } refused)
            {
                LogLevel failureLevel =
                    refused.ErrorCode == "operation_failed" ? LogLevel.Error : LogLevel.Warning;
                string outcome = failureLevel == LogLevel.Error ? "failed" : "refused";
                logger.Log(
                    failureLevel,
                    "Hub result {Hub}.{Method} {Outcome} reason={Reason} user={User} device={Device} in {Elapsed}ms",
                    hub,
                    method,
                    outcome,
                    refused.ErrorCode,
                    user,
                    device,
                    stopwatch.ElapsedMilliseconds
                );
                return result;
            }

            logger.Log(
                level,
                "Hub result {Hub}.{Method} ok user={User} device={Device} in {Elapsed}ms",
                hub,
                method,
                user,
                device,
                stopwatch.ElapsedMilliseconds
            );
            return result;
        }
        catch (HubException exception)
        {
            logger.LogWarning(
                "Hub result {Hub}.{Method} refused reason={Reason} user={User} device={Device} in {Elapsed}ms",
                hub,
                method,
                exception.GetType().Name,
                user,
                device,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Hub result {Hub}.{Method} failed exception={Exception} user={User} device={Device} in {Elapsed}ms",
                hub,
                method,
                exception.GetType().Name,
                user,
                device,
                stopwatch.ElapsedMilliseconds
            );
            throw;
        }
    }

    public async Task OnConnectedAsync(
        HubLifetimeContext context,
        Func<HubLifetimeContext, Task> next
    )
    {
        if (IsSessionHub(context.Hub))
            logger.LogInformation(
                "Hub connected {Hub} user={User} device={Device} connection={Connection}",
                context.Hub.GetType().Name,
                context.Context.User.UserId(),
                SessionLogText.DeviceOf(context.Context),
                context.Context.ConnectionId
            );

        await next(context);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next
    )
    {
        if (IsSessionHub(context.Hub))
            logger.Log(
                exception is null ? LogLevel.Information : LogLevel.Warning,
                "Hub disconnected {Hub} user={User} device={Device} connection={Connection} reason={Reason}",
                context.Hub.GetType().Name,
                context.Context.User.UserId(),
                SessionLogText.DeviceOf(context.Context),
                context.Context.ConnectionId,
                exception?.GetType().Name ?? "closed"
            );

        await next(context, exception);
    }

    private static bool IsSessionHub(Hub hub) =>
        hub is DeviceHub or MusicHub or VideoHub or CastHub;
}
