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
using Microsoft.Extensions.Hosting;
using Moq;
using NoMercy.Database;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Hardware;

/// <summary>
/// Issue #350: the speed index consumers get from DI must follow the
/// benchmark's cache, which is replaced after every probe and after a full
/// run, instead of the empty snapshot taken at first resolve.
/// </summary>
public class SpeedIndexProviderTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];

    private SpeedIndex _published = new([]);

    private ServiceProvider BuildProvider()
    {
        Mock<IHardwareBenchmark> benchmark = new();
        benchmark.Setup(b => b.GetCachedIndex()).Returns(() => _published);

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();
        services.AddDbContextFactory<MediaContext>(o => o.UseInMemoryDatabase("speed-media"));
        services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("speed-app"));
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = "ffmpeg";
            opts.FfprobePathOverride = "ffprobe";
        });
        services.AddSingleton(benchmark.Object);

        ServiceProvider provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    public void Dispose()
    {
        foreach (ServiceProvider provider in _providers)
            provider.Dispose();
        _providers.Clear();
    }

    [Fact]
    public void ResolvedSpeedIndex_AfterBenchmarkPublishes_SeesNewMeasurements()
    {
        ServiceProvider provider = BuildProvider();
        ISpeedIndexProvider resolved = provider.GetRequiredService<ISpeedIndexProvider>();

        _published = new(
            new()
            {
                [new(VideoCodecType.H264, "h264_nvenc", 1920, "RTX")] = new(
                    Fps: 240,
                    SpeedMultiplier: 10,
                    MeasuredAt: DateTime.UtcNow
                ),
            }
        );

        resolved
            .Current.Measurements.Should()
            .HaveCount(
                1,
                "the speed index consumers resolve must show what the benchmark published, not the boot-time snapshot"
            );
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }
}
