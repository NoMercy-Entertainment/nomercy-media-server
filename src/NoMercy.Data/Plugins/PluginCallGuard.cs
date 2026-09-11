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

namespace NoMercy.Data.Plugins;

/// <summary>
/// What every plugin-facing facade does with a failure nobody planned for - a
/// mount that went away, a locked database file. The exception is logged at
/// Warning on the server's own record and the caller is handed a refusal, or
/// the absence its member can express: a plugin sweeping a library unattended
/// loses one track that way instead of its whole sweep.
/// <para>
/// Cancellation the caller asked for is never swallowed anywhere here.
/// </para>
/// </summary>
public static class PluginCallGuard
{
    /// <summary>For a member that can say why it could not answer.</summary>
    public static async Task<T> RunAsync<T>(
        string operation,
        Func<Task<T>> body,
        Func<string, T> refuse,
        ILogger logger
    )
    {
        try
        {
            return await body();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(logger, exception, operation);
            return refuse($"the server could not complete this call: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// For a member with no refusal channel: the failure reads as the absence
    /// the member already has a word for - false, or null.
    /// </summary>
    public static async Task<T> RunOrAsync<T>(
        string operation,
        Func<Task<T>> body,
        T fallback,
        ILogger logger
    )
    {
        try
        {
            return await body();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(logger, exception, operation);
            return fallback;
        }
    }

    /// <summary>For a member that returns nothing, where a failure is a no-op.</summary>
    public static async Task RunAsync(string operation, Func<Task> body, ILogger logger)
    {
        try
        {
            await body();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(logger, exception, operation);
        }
    }

    private static void LogUnexpected(ILogger logger, Exception exception, string operation) =>
        logger.LogWarning(exception, "{Operation} failed inside the server", operation);
}
