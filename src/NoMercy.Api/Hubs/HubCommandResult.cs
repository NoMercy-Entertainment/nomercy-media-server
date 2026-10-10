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

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace NoMercy.Api.Hubs;

public sealed record HubCommandResult(
    [property: JsonProperty("ok")] bool Ok,
    [property: JsonProperty("error_code")] string? ErrorCode,
    [property: JsonProperty("message")] string? Message,
    [property: JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        string? Status = null
)
{
    public static HubCommandResult Success() => new(true, null, null);

    public static HubCommandResult Invalid(string message) => new(false, "invalid_input", message);

    public static HubCommandResult NotFound(string message) => new(false, "not_found", message);

    public static HubCommandResult Forbidden(string message) => new(false, "forbidden", message);

    public static HubCommandResult Failed() =>
        new(false, "operation_failed", "The command could not be completed.");

    public static HubCommandResult FromWakeStatus(string status) =>
        status switch
        {
            "wake_sent" => new(true, null, null, status),
            "not_owned" => new(false, "not_found", "Device was not found for this user.", status),
            "no_route" => new(
                false,
                "operation_failed",
                "No route to the device was available.",
                status
            ),
            "invalid_input" => new(false, "invalid_input", "Device id is invalid.", status),
            _ => new(false, "operation_failed", "The device could not be woken.", status),
        };

    public static HubCommandResult Execute(Action action, ILogger logger)
    {
        try
        {
            action();
            return Success();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hub command failed");
            return Failed();
        }
    }

    public static async Task<HubCommandResult> ExecuteAsync(Func<Task> action, ILogger logger)
    {
        try
        {
            await action();
            return Success();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Hub command failed");
            return Failed();
        }
    }
}
