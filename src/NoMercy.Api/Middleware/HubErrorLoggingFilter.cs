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

using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Information;

namespace NoMercy.Api.Middleware;

/// <summary>
/// SignalR hub filter that logs errors for invalid method calls, wrong arguments, and exceptions.
/// This helps debug client-side calls to hub methods that don't exist or have incorrect parameters.
/// </summary>
public class HubErrorLoggingFilter(ILogger<HubErrorLoggingFilter> logger, IUserCache userCache)
    : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        string hubName = invocationContext.Hub.GetType().Name;
        string methodName = invocationContext.HubMethodName;
        string connectionId = invocationContext.Context.ConnectionId;

        string? guid = invocationContext.Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (guid == null)
        {
            logger.LogInformation(
                "[Unknown User]: [{HubName}] No user identifier found in claims.",
                hubName
            );
            return await next(invocationContext);
        }

        if (!Guid.TryParse(guid, out Guid userId))
        {
            logger.LogInformation(
                "[{HubName}] Malformed user GUID claim '{Guid}' on connection {ConnectionId}",
                [hubName, guid, connectionId]
            );
            return await next(invocationContext);
        }
        User? user = userCache.Users.FirstOrDefault(x => x.Id.Equals(userId));

        if (user == null)
        {
            logger.LogInformation(
                "[Unknown User]: [{HubName}] User with ID {UserId} not found.",
                [hubName, userId]
            );
            return await next(invocationContext);
        }

        try
        {
            // Execute the hub method with SQLite retry protection.
            // FlexLabs.Upsert (used in VideoHub.SetTime etc.) calls ExecuteSqlRawAsync
            // which bypasses the EF Core execution strategy's retry pipeline.
            return await SqliteRetryingExecutionStrategy.ExecuteWithRetryAsync(async () =>
                await next(invocationContext)
            );
        }
        catch (HubException hubEx)
        {
            // HubException is thrown intentionally to send error messages to clients
            logger.LogInformation(
                "{Name}: [{HubName}.{MethodName}] Hub exception: {Message}",
                [user.Name, hubName, methodName, hubEx.Message]
            );
            throw; // Re-throw to send to client
        }
        catch (InvalidOperationException invalidOpEx)
            when (invalidOpEx.Message.Contains("does not exist"))
        {
            // This catches when a client calls a method that doesn't exist
            logger.LogInformation(
                "{Name}: [{HubName}] Method '{MethodName}' does not exist (connection {ConnectionId}); hub methods are the public Task methods of the hub class",
                [user.Name, hubName, methodName, connectionId]
            );

            throw ClientError($"Method '{methodName}' does not exist on hub '{hubName}'");
        }
        catch (ArgumentException argEx)
        {
            // Parameter binding errors: wrong types, missing required parameters.
            logger.LogWarning(
                argEx,
                "{Name}: [{HubName}.{MethodName}] Invalid arguments ({Arguments})",
                user.Name,
                hubName,
                methodName,
                DescribeArguments(invocationContext)
            );

            throw ClientError($"Invalid arguments for method '{methodName}': {argEx.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "{Name}: [{HubName}.{MethodName}] Unhandled exception ({Arguments})",
                user.Name,
                hubName,
                methodName,
                DescribeArguments(invocationContext)
            );

            throw ClientError($"An error occurred calling '{methodName}': {ex.Message}");
        }
    }

    private static string DescribeArguments(HubInvocationContext invocationContext) =>
        invocationContext.HubMethodArguments.Count == 0
            ? "no arguments"
            : string.Join(
                ", ",
                invocationContext.HubMethodArguments.Select(
                    (arg, index) => $"arg{index}: {arg?.GetType().Name ?? "null"}"
                )
            );

    /// <summary>The detail reaches the client only in development; production says nothing more.</summary>
    private static HubException ClientError(string developmentDetail) =>
        new(Config.IsDev ? developmentDetail : "An internal error occurred");
}
