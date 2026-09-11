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

namespace NoMercy.Plugins;

/// <summary>
/// Builds the host's <see cref="IPluginMusicAnalysisWriter" /> for one plugin.
/// <para>
/// Unlike <c>IPluginLibraryWriterFactory</c>, this never returns null: the
/// grant question here is whether a plugin declared
/// <c>PluginHookCapability.MusicAnalysisWrite</c>, which the plugin host
/// checks before handing the writer out at all, not something this factory
/// re-checks per call.
/// </para>
/// <para>
/// The host's one rule for facade lifetimes: a facade with per-plugin state
/// is cached per plugin id by its factory, one that only stamps the plugin id
/// - this writer - is built per call, and one with no per-plugin state at all
/// is a single shared instance.
/// </para>
/// </summary>
public interface IPluginMusicAnalysisWriterFactory
{
    IPluginMusicAnalysisWriter CreateFor(Ulid pluginId);
}
