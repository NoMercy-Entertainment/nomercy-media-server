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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The work a plugin may ask the server to do.
/// <para>
/// A word the host knows, never the name of a job type. Plugins used to reach
/// the server's own job classes by name and drive them with reflection, which
/// broke silently four times in three days: the class moved, the plugin kept
/// compiling, and the work simply stopped happening.
/// </para>
/// </summary>
public enum PluginJobKind
{
    Rescan,
    FetchImages,
    RefreshMetadata,
    Encode,
}
