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

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace NoMercy.Api.Hubs.Filters;

/// <summary>
/// Keeps successful legacy query payloads while giving failed hub invocations
/// a structured completion result instead of a connection-level exception.
/// </summary>
public sealed class HubCommandResultFilter(ILogger<HubCommandResultFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        try
        {
            if (invocationContext.Hub is PluginHub && invocationContext.HubMethodName == "Send")
            {
                IReadOnlyList<object?> arguments = invocationContext.HubMethodArguments;
                if (
                    arguments.Count < 2
                    || arguments[0] is not string pluginId
                    || !Ulid.TryParse(pluginId, out Ulid id)
                    || id == Ulid.Empty
                    || arguments[1] is not string method
                    || string.IsNullOrWhiteSpace(method)
                )
                    return HubCommandResult.Invalid("Plugin id and method are required.");

                object? routed = await next(invocationContext);
                return routed is false
                    ? HubCommandResult.NotFound("Plugin could not receive the command.")
                    : routed;
            }

            return await next(invocationContext);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Hub method {Hub}.{Method} failed",
                invocationContext.Hub.GetType().Name,
                invocationContext.HubMethodName
            );
            return HubCommandResult.Failed();
        }
    }
}
