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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.Encoder.Analysis;
using NoMercy.MediaProcessing.Files.AudioTracks;
using NoMercy.Storage;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Jobs;

/// <summary>
/// Stores the audio tracks of every video file that has none yet. A scan queues
/// it after storing files; it is idempotent, so queuing it again is harmless.
/// </summary>
[Serializable]
public class VideoFileAudioTrackBackfillJob : IShouldQueue, IJobStorageInjector
{
    public string QueueName => "file";
    public int Priority => 1;

    private VideoFileAudioTrackBackfill? _backfill;

    public void InjectStorageServices(IServiceProvider serviceProvider)
    {
        _backfill = new(
            serviceProvider.GetRequiredService<IDbContextFactory<MediaContext>>(),
            serviceProvider.GetRequiredService<IStorageFactory>(),
            serviceProvider.GetRequiredService<IMediaAnalyzer>()
        );
    }

    public async Task Handle()
    {
        if (_backfill is null)
            return;

        int filled = await _backfill.RunAsync();

        Logger.App(
            $"[AudioTrackBackfill] stored audio tracks for {filled} video file(s)",
            LogEventLevel.Information
        );
    }
}
