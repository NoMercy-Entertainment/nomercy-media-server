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

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// Builds a <see cref="PluginAudioTools" /> bound to one plugin's id. A
/// singleton holding the collaborators every instance needs.
/// <para>
/// Unlike <see cref="PluginMusicAnalysisWriterFactory" />, the instances are
/// cached per plugin rather than created per call: the one-ffmpeg-at-a-time
/// guard is a field on the instance, and a guard a caller can get a fresh
/// copy of guards nothing.
/// </para>
/// </summary>
/// <param name="storage">The default, library-scoped storage: track files, and the ffmpeg binary's own presence.</param>
/// <param name="derivedStorage">Scoped to the derived-audio root, for stem scratch files and derived inputs.</param>
public class PluginAudioToolsFactory(
    EncoderOptions options,
    IProcessRunner processRunner,
    IStorage storage,
    IStorageDriver storageDriver,
    [FromKeyedServices("derived-audio")] IStorage derivedStorage,
    IDerivedAudioStore store,
    IPluginMusicAnalysisWriterFactory writerFactory,
    IDbContextFactory<MediaContext> contextFactory,
    IFfmpegCapabilityProbe capabilityProbe,
    ILogger<PluginAudioTools> logger
) : IPluginAudioToolsFactory
{
    private readonly ConcurrentDictionary<Ulid, PluginAudioTools> _perPlugin = new();

    public IPluginAudioTools CreateFor(Ulid pluginId) =>
        _perPlugin.GetOrAdd(
            pluginId,
            id => new PluginAudioTools(
                id,
                options,
                processRunner,
                storage,
                storageDriver,
                derivedStorage,
                store,
                writerFactory,
                contextFactory,
                capabilityProbe,
                logger
            )
        );
}
