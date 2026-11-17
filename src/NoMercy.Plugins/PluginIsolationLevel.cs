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

namespace NoMercy.PluginSdk;

/// <summary>
/// What the owner is told about the boundary they are consenting across.
/// <para>
/// A permissions page that lists capabilities and says nothing about isolation
/// invites the reading that refusing one contains the plugin. In stage one it
/// does not: a plugin runs inside the server, and what the owner approves is
/// which doors are unlocked, not how thick the walls are. Saying so is the
/// difference between an informed yes and a yes based on a wrong picture.
/// </para>
/// <para>
/// Stage two changes these constants and nothing else on that page.
/// </para>
/// </summary>
public static class PluginIsolationLevel
{
    public const string Current = "in-process";

    /// <summary>A key the clients translate, never a baked sentence.</summary>
    public const string NoticeKey = "plugins.permissions.in_process_notice";

    /// <summary>
    /// The English source for <see cref="NoticeKey" />. Here so the wording
    /// lives beside the level it describes; a translator reads it from the
    /// locale file like any other string.
    /// </summary>
    public const string EnglishNotice =
        "Runs inside the server: permissions are checked, not isolated.";

    /// <summary>
    /// Said when the owner asks for out-of-process for any plugin.
    /// <para>
    /// The runtime is newer than the setting and does not carry everything
    /// yet. Returning a plain OK let the owner believe every plugin would keep
    /// working there, when one that needs what the runtime lacks is refused.
    /// The key keeps its old name so clients that already translate it do not
    /// lose the line.
    /// </para>
    /// </summary>
    public const string PendingKey = "plugins.runtime.out_of_process_pending";

    public const string EnglishPending =
        "Saved. A plugin set to its own process runs there, unless it needs something that process does not carry yet; that plugin is refused until it is set back to in-process.";

    /// <summary>
    /// Said for one plugin that is set to its own process and uses something
    /// that process does not carry, so it is refused rather than started there
    /// broken. The refusal names what it would lose.
    /// </summary>
    public const string NeedsInProcessKey = "plugins.runtime.needs_in_process";

    public const string EnglishNeedsInProcess =
        "This plugin needs the server's own process. Set it to in-process to run it.";
}
