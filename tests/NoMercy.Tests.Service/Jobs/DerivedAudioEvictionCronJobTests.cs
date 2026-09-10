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

using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.NmSystem.Configuration;
using NoMercy.Service.Jobs;
using NoMercyQueue.Core;

namespace NoMercy.Tests.Service.Jobs;

/// <summary>
/// The hourly counterpart to <see cref="DerivedAudioStore" />'s eviction
/// policy: this job's whole job is calling it with the cap the dashboard
/// configured and the fixed 24-hour grace window, once per run.
/// </summary>
public class DerivedAudioEvictionCronJobTests
{
    [Fact]
    public async Task ExecuteAsync_CallsEvictAsync_WithConfiguredCapAndTwentyFourHourGrace_ExactlyOnce()
    {
        long originalCap = RuntimeServerSettings.Current.DerivedAudioCapBytes;
        RuntimeServerSettings.Current.DerivedAudioCapBytes = 20L * 1024 * 1024 * 1024;
        try
        {
            Mock<IDerivedAudioStore> store = new();
            store
                .Setup(s =>
                    s.EvictAsync(
                        It.IsAny<long>(),
                        It.IsAny<TimeSpan>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ReturnsAsync(0L);

            DerivedAudioEvictionCronJob job = new(store.Object);

            await job.ExecuteAsync(string.Empty);

            store.Verify(
                s =>
                    s.EvictAsync(
                        RuntimeServerSettings.Current.DerivedAudioCapBytes,
                        TimeSpan.FromHours(24),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        }
        finally
        {
            RuntimeServerSettings.Current.DerivedAudioCapBytes = originalCap;
        }
    }

    [Fact]
    public void CronExpression_IsHourly_AndJobNameIsSet()
    {
        Mock<IDerivedAudioStore> store = new();
        DerivedAudioEvictionCronJob job = new(store.Object);

        job.CronExpression.Should().Be(new CronExpressionBuilder().Hourly());
        job.JobName.Should().Be("Derived Audio Eviction");
    }
}
