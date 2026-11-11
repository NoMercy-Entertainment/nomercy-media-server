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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Monitoring;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Trait("Category", "Unit")]
public class ImportQueuePriorityTests
{
    [Fact]
    public void AudioImportJob_priority_is_below_every_video_and_person_import()
    {
        int audio = new AudioImportJob().Priority;

        Assert.True(audio < new MovieImportJob().Priority);
        Assert.True(audio < new ShowImportJob().Priority);
        Assert.True(audio < new PersonRefreshJob().Priority);
    }

    [Fact]
    public void MusicTrackRepairJob_priority_is_below_audio_and_video_imports()
    {
        int repair = new MusicTrackRepairJob().Priority;

        Assert.True(repair < new AudioImportJob().Priority);
        Assert.True(repair < new MovieImportJob().Priority);
        Assert.True(repair < new ShowImportJob().Priority);
    }

    [Fact]
    public async Task Music_file_jobs_wait_when_playback_is_active()
    {
        MediaActivityMonitor monitor = new();
        monitor.Touch();
        ServiceCollection services = new();
        services.AddSingleton(monitor);
        services.AddSingleton(Mock.Of<IStorageFactory>());
        services.AddSingleton(Mock.Of<IStorageDriver>());
        services.AddSingleton(Mock.Of<IAudioFingerprinter>());
        services.AddSingleton(Mock.Of<ILoggerFactory>());
        using ServiceProvider provider = services.BuildServiceProvider();

        foreach (Type jobType in new[] { typeof(AudioImportJob), typeof(MusicTrackRepairJob) })
        {
            AbstractMusicFolderJob job = (AbstractMusicFolderJob)
                ActivatorUtilities.CreateInstance(provider, jobType);
            Task pause = job.ThrottleForPlaybackAsync(TimeSpan.FromMilliseconds(100));

            Assert.False(pause.IsCompleted);
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
