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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Hooks;

/// <summary>
/// What plugins know about a title that the native provider did not answer.
/// <para>
/// Native first, always. TMDB runs, and a plugin fills the fields it left empty
/// — it never replaces one. A hook that could overwrite the provider would make
/// a library's metadata depend on which plugin happened to load first, and the
/// viewer would have no way to tell where a wrong year came from.
/// </para>
/// </summary>
public interface IPluginMetadataResolver
{
    /// <summary>
    /// The first non-null answer per field, across every plugin that declares
    /// the hook. Null when no plugin answered at all.
    /// </summary>
    Task<MediaMetadata?> ResolveAsync(string title, MediaType type, CancellationToken ct = default);
}
