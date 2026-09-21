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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Revocation;

/// <summary>
/// Asked before a plugin runs: is this exact build still allowed.
/// <para>
/// A server that cannot reach nomercy.tv keeps working until its list is a
/// week old, then pauses every plugin, free ones included. Not as a penalty:
/// after a week it can no longer honestly say none of them was revoked, and
/// answering yes to a question it cannot check is the failure this exists to
/// prevent. Nothing is uninstalled and nothing the owner approved is lost.
/// </para>
/// </summary>
public class PluginRevocationGate(IPluginRevocationStore store, TimeProvider clock)
{
    public const int StaleAfterDays = 7;

    public PluginRefusal? Check(Ulid pluginId, string packageHash)
    {
        PluginRevocationList list = store.Current;
        PluginRevocationEntry? revoked = list.Find(pluginId, packageHash);

        if (revoked is not null)
            return new(
                PluginRefusalCodes.Revoked,
                pluginId.ToString(),
                "The plugin stopped because this build was revoked.",
                $"NoMercy revoked this build: {revoked.Reason}.",
                "Install the newest version. A revocation names one build, so the publisher's next release installs normally.",
                PluginRefusalSeverity.Blocked
            );

        if (clock.GetUtcNow() - list.IssuedAt <= TimeSpan.FromDays(StaleAfterDays))
            return null;

        return new(
            PluginRefusalCodes.RevocationListStale,
            pluginId.ToString(),
            "Every plugin on this server is paused.",
            $"This server has not reached NoMercy for more than {StaleAfterDays} days, so it cannot tell whether a plugin was revoked.",
            "Connect the server to the internet. Nothing was uninstalled and nothing you approved was lost; the plugins start again on the next successful check.",
            PluginRefusalSeverity.Blocked
        );
    }
}
