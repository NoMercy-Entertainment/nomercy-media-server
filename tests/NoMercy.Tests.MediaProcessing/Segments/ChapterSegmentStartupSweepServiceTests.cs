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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.MediaProcessing.Segments;
using NoMercy.NmSystem.Monitoring;

namespace NoMercy.Tests.MediaProcessing.Segments;

[Trait("Category", "Unit")]
public sealed class ChapterSegmentStartupSweepServiceTests : IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly Mock<IHostApplicationLifetime> _lifetime = new();
    private readonly Mock<IChapterSegmentWriter> _writer = new();
    private readonly TaskCompletionSource _swept = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public ChapterSegmentStartupSweepServiceTests()
    {
        _lifetime.Setup(l => l.ApplicationStarted).Returns(_started.Token);
        _writer
            .Setup(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _swept.TrySetResult())
            .ReturnsAsync(3);
    }

    public void Dispose() => _started.Dispose();

    [Fact]
    public async Task Boot_is_not_blocked_and_the_sweep_waits_for_the_host_to_start()
    {
        using ChapterSegmentStartupSweepService service = Service(TimeSpan.Zero);

        Task start = service.StartAsync(CancellationToken.None);

        start.IsCompleted.Should().BeTrue("StartAsync must return before the sweep runs");
        await Task.Delay(100);
        _writer.Verify(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()), Times.Never);

        await _started.CancelAsync();
        await _swept.Task.WaitAsync(TimeSpan.FromSeconds(10));

        _writer.Verify(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_sweep_waits_out_the_grace_period_after_start()
    {
        await _started.CancelAsync();
        using ChapterSegmentStartupSweepService service = Service(TimeSpan.FromMinutes(5));

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        _writer.Verify(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()), Times.Never);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_during_the_wait_ends_quietly_without_a_sweep()
    {
        using ChapterSegmentStartupSweepService service = Service(TimeSpan.Zero);
        await service.StartAsync(CancellationToken.None);

        Func<Task> stop = () => service.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
        _writer.Verify(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failing_sweep_is_logged_and_does_not_crash_the_host()
    {
        _writer
            .Setup(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()))
            .Callback(() => _swept.TrySetResult())
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        await _started.CancelAsync();
        using ChapterSegmentStartupSweepService service = Service(TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        await _swept.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Func<Task> stop = () => service.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public void The_container_builds_it_with_the_default_grace()
    {
        ServiceCollection services = new();
        services.AddSingleton(_lifetime.Object);
        services.AddSingleton(_writer.Object);
        services.AddSingleton<MediaActivityMonitor>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddHostedService<ChapterSegmentStartupSweepService>();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider
            .GetServices<IHostedService>()
            .Should()
            .ContainSingle(s => s is ChapterSegmentStartupSweepService);
    }

    private ChapterSegmentStartupSweepService Service(TimeSpan grace) =>
        new(
            _lifetime.Object,
            _writer.Object,
            new MediaActivityMonitor(),
            NullLogger<ChapterSegmentStartupSweepService>.Instance,
            grace
        );
}
