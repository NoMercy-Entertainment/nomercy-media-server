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

using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Recordings;
using NoMercy.Providers.Lyrics;

namespace NoMercy.MediaProcessing.Jobs.MediaJobs;

public class LyricFetchJob : AbstractLyricJob
{
    public override string QueueName => "music";
    public override int Priority => 0;

    public override async Task Handle()
    {
        await Task.Delay(1000); // wait for
        await using MediaContext mediaContext = new();
        RecordingRepository recordingRepository = new(mediaContext);

        LyricsFetchResult result = await new LyricsAggregator().SearchLyrics(Track);
        if (result.Lines is null)
            return;

        await recordingRepository.UpdateTrackLyricsAsync(
            Track,
            JsonConvert.SerializeObject(result.Lines),
            result.Source,
            result.Synced ? LyricsTimings.Provider : LyricsTimings.None
        );
    }
}
