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

namespace NoMercy.Data.Plugins;

/// <summary>
/// A path a plugin named inside its own data folder, after
/// <see cref="PluginGraphTokens.ResolvePluginPath" />: where it is on disk, or
/// the reason it may not be used.
/// </summary>
/// <param name="FullPath">The absolute path, not escaped for anything. Empty when refused.</param>
/// <param name="Refusal">Why the path is not one inside the folder, in words the plugin author can act on. Null when it is.</param>
internal sealed record ResolvedPluginPath(string FullPath, string? Refusal);
