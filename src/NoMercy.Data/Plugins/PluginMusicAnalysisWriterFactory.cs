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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Builds a <see cref="PluginMusicAnalysisWriter" /> bound to one plugin's
/// id. A singleton holding only the two collaborators every writer needs -
/// the plugin id itself is per-call, not held here.
/// </summary>
public class PluginMusicAnalysisWriterFactory(
    IDbContextFactory<MediaContext> contextFactory,
    IDerivedAudioStore store
) : IPluginMusicAnalysisWriterFactory
{
    public IPluginMusicAnalysisWriter CreateFor(Ulid pluginId) =>
        new PluginMusicAnalysisWriter(pluginId, contextFactory, store);
}
