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
using Microsoft.Extensions.Logging;
using NoMercy.Data.Services.Music;
using NoMercy.Database;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Builds a <see cref="PluginLyricsWriter" /> bound to one plugin's id. A
/// singleton holding what every writer needs; the plugin id is per call.
/// </summary>
public class PluginLyricsFactory(
    IDbContextFactory<MediaContext> contextFactory,
    ILyricsTextCandidates textCandidates,
    ILogger<PluginLyricsWriter> logger
) : IPluginLyricsFactory
{
    public IPluginLyrics CreateFor(Ulid pluginId) =>
        new PluginLyricsWriter(pluginId, contextFactory, textCandidates, logger);
}
