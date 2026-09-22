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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// Says which contract member a plugin reached for that is no longer there.
/// <para>
/// The host calls into plugin code from ten places. A plugin built against an
/// older SDK fails at whichever of them runs first, with a runtime message
/// that names a method and not the contract it belonged to. Every one of those
/// places logs the same refusal so the author reads the same sentence wherever
/// it surfaces, which is the point of design section 3.9.
/// </para>
/// </summary>
public static class PluginStaleMemberLog
{
    /// <summary>
    /// Whether this failure is a plugin reaching a member that does not exist
    /// in this server's SDK, and if so, says so in the log. Returns false for
    /// anything else, so a caller keeps whatever it already does with an
    /// ordinary failure.
    /// </summary>
    public static bool Explain(ILogger logger, Ulid pluginId, Exception exception, string doing)
    {
        MissingMemberException? missing = Find(exception);
        if (missing is null)
            return false;

        PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
            pluginId.ToString(),
            missing.Message
        );

        logger.LogError(
            "Plugin {Plugin} was skipped while {Doing}. {What} {Why} {Fix}",
            pluginId,
            doing,
            refusal.What,
            refusal.Why,
            refusal.Fix
        );

        return true;
    }

    /// <summary>
    /// The same failure written for somewhere that carries a message rather
    /// than a log call: the refusal when a plugin reached a member the contract
    /// does not have, and the exception's own message otherwise.
    /// <para>
    /// The torrent downloader on a real server produced <c>Method not found:
    /// 'NoMercy.Events.IEventBus IPluginContext.get_EventBus()'</c>. Accurate
    /// and useless: it names a getter rather than the capability to declare
    /// instead, and it reads as a server fault rather than a plugin built
    /// against something older.
    /// </para>
    /// </summary>
    public static string Describe(Ulid pluginId, Exception exception)
    {
        MissingMemberException? missing = Find(exception);

        if (missing is null)
            return exception.Message;

        PluginRefusal refusal = PluginRefusalMessages.RemovedContractMember(
            pluginId.ToString(),
            missing.Message
        );

        return $"{refusal.What} {refusal.Why} {refusal.Fix}";
    }

    /// <summary>
    /// A missing member is the innermost thing that went wrong, and the host
    /// calls a plugin through delegates and tasks that wrap it on the way out.
    /// </summary>
    private static MissingMemberException? Find(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is MissingMemberException missing)
                return missing;

            if (exception is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                        return found;
                }

                return null;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
