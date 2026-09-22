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

namespace NoMercy.PluginSdk.Runtime;

/// <summary>
/// Which file a binary name means.
/// <para>
/// A plugin declaring <c>ffmpeg</c> must run the ffmpeg the owner read on the
/// permissions page, never the first hit on PATH. A child process runs as the
/// server's own user, so a PATH search is how a plugin runs something nobody
/// agreed to on a machine where PATH has been edited.
/// </para>
/// </summary>
public interface IPluginApprovedBinaries
{
    /// <summary>The absolute path this name resolves to, or null when nothing approved it.</summary>
    string? PathFor(Ulid pluginId, string name);
}
