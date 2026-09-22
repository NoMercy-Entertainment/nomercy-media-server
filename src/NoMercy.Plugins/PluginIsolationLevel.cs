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
}
