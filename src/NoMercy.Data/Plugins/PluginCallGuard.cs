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
/// <para>
/// Which plugin and which member are logged as two structured properties
/// rather than one sentence, so a log pipeline can answer "everything this
/// plugin did" without parsing the message. <c>PluginId</c> is always a ULID
/// string: the derived-audio facade is one object every plugin shares and has
/// no plugin of its own to name, so it passes the empty ULID
/// (<c>PluginDerivedAudio.SharedPluginId</c>) rather than leaving the property
/// out or inventing a word - a missing field reads as a gap in the pipeline,
/// and a word reads as a parse failure.
/// </para>
/// <para>
/// One template, <c>"plugin {PluginId}: {Member} failed inside the server"</c>,
/// for every entry: the shared facade's put used to carry a distinctive
/// sentence of its own, and a pipeline could then match on one wording or the
/// other but never on both.
/// </para>
/// </summary>
public static class PluginCallGuard
{
    /// <summary>For a member that can say why it could not answer.</summary>
    public static async Task<T> RunAsync<T>(
        string pluginId,
        string member,
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
            LogUnexpected(logger, exception, pluginId, member);
            return refuse($"the server could not complete this call: {exception.GetType().Name}");
        }
    }

    /// <summary>
    /// For a member with no refusal channel: the failure reads as the absence
    /// the member already has a word for - false, or null.
    /// </summary>
    public static async Task<T> RunOrAsync<T>(
        string pluginId,
        string member,
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
            LogUnexpected(logger, exception, pluginId, member);
            return fallback;
        }
    }

    /// <summary>For a member that returns nothing, where a failure is a no-op.</summary>
    public static async Task RunAsync(
        string pluginId,
        string member,
        Func<Task> body,
        ILogger logger
    )
    {
        try
        {
            await body();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(logger, exception, pluginId, member);
        }
    }

    private static void LogUnexpected(
        ILogger logger,
        Exception exception,
        string pluginId,
        string member
    ) =>
        logger.LogWarning(
            exception,
            "plugin {PluginId}: {Member} failed inside the server",
            pluginId,
            member
        );
}
