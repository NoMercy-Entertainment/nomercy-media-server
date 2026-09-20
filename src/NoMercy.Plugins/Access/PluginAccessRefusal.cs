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

namespace NoMercy.Plugins.Access;

/// <summary>
/// One wording, so a plugin's page and a plugin's own REST route refuse the
/// same caller the same way.
/// </summary>
public static class PluginAccessRefusal
{
    public static PluginRefusal For(Ulid pluginId) =>
        new(
            PluginRefusalCodes.AccessDenied,
            pluginId.ToString(),
            "That did not open.",
            "This plugin is not shared with your account on this server.",
            "Ask the owner of this server to share it, or buy it on nomercy.tv with your own account.",
            PluginRefusalSeverity.Blocked
        );
}
