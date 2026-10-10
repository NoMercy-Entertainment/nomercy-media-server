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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.TvShows;
using NoMercy.Encoder.ContentAnalysis.Fingerprinting;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.EventHandlers;

public class IntroDetectionSubscriberTests
{
    [Fact]
    public async Task EncodesArrivingDuringScan_AreIncludedInFollowUpScan()
    {
        TaskCompletionSource firstScanStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource releaseFirstScan = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int fingerprintCalls = 0;

        Mock<IAudioFingerprinter> fingerprinter = new();
        fingerprinter
            .Setup(f =>
                f.FingerprintAsync(
                    It.IsAny<string>(),
                    It.IsAny<FingerprintWindow>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (string _, FingerprintWindow _, CancellationToken _) =>
                {
                    if (Interlocked.Increment(ref fingerprintCalls) == 1)
                    {
                        firstScanStarted.SetResult();
                        await releaseFirstScan.Task;
                    }

                    return new AudioFingerprint([1, 2, 3], TimeSpan.FromSeconds(1), TimeSpan.Zero);
                }
            );

        Mock<IIntroDetector> detector = new();
        IntroMarker intro = new(TimeSpan.Zero, TimeSpan.FromSeconds(10), 0.9);
        IntroMarker outro = new(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(21), 0.9);
        detector
            .Setup(d => d.DetectIntro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(intro);
        detector
            .Setup(d => d.DetectOutro(It.IsAny<IReadOnlyList<AudioFingerprint>>()))
            .Returns(outro);

        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string parent, string[] child) => parent + "/" + child[0]);
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        ServiceProvider services = new ServiceCollection()
            .AddScoped(_ => new MediaContext(options))
            .AddSingleton(fingerprinter.Object)
            .AddSingleton(detector.Object)
            .BuildServiceProvider();
        TaskCompletionSource initialEventsHandled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource lateEventsHandled = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        int completedScopes = 0;
        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory
            .Setup(f => f.CreateScope())
            .Returns(() =>
                new CountingScope(
                    services.CreateScope(),
                    () =>
                    {
                        int count = Interlocked.Increment(ref completedScopes);
                        if (count == 2)
                            initialEventsHandled.TrySetResult();
                        if (count == 4)
                            lateEventsHandled.TrySetResult();
                    }
                )
            );
        await using (services)
        {
            await using (AsyncServiceScope scope = services.CreateAsyncScope())
            {
                MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();
                for (int id = 1; id <= 5; id++)
                {
                    context.Episodes.Add(
                        new Episode
                        {
                            Id = id,
                            TvId = 1,
                            SeasonId = 1,
                            SeasonNumber = 1,
                            EpisodeNumber = id,
                        }
                    );
                    if (id <= 3)
                        AddVideoFile(context, id);
                }
                await context.SaveChangesAsync();
            }

            InMemoryEventBus bus = new();
            IntroDetectionSubscriber subscriber = new(
                bus,
                scopeFactory.Object,
                NullLogger<IntroDetectionSubscriber>.Instance,
                storage.Object
            );
            await subscriber.StartAsync(CancellationToken.None);

            try
            {
                foreach (int id in new[] { 1, 2, 3 })
                    await bus.PublishAsync(Completed(id));

                await firstScanStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await initialEventsHandled.Task.WaitAsync(TimeSpan.FromSeconds(10));

                await using (AsyncServiceScope scope = services.CreateAsyncScope())
                {
                    MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();
                    AddVideoFile(context, 4);
                    AddVideoFile(context, 5);
                    await context.SaveChangesAsync();
                }

                await bus.PublishAsync(Completed(4));
                await bus.PublishAsync(Completed(5));
                await lateEventsHandled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                releaseFirstScan.TrySetResult();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                await using AsyncServiceScope scope = services.CreateAsyncScope();
                MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();
                if (await context.ContentSegments.CountAsync() == 10)
                    break;
                await Task.Delay(20);
            }

            await using (AsyncServiceScope scope = services.CreateAsyncScope())
            {
                MediaContext context = scope.ServiceProvider.GetRequiredService<MediaContext>();
                for (int id = 1; id <= 5; id++)
                {
                    List<ContentSegment> segments = await context
                        .ContentSegments.Where(s => s.EpisodeId == id)
                        .ToListAsync();
                    segments
                        .Select(s => s.SegmentType)
                        .Should()
                        .BeEquivalentTo([ContentSegmentType.Intro, ContentSegmentType.Outro]);
                }
            }

            await subscriber.StopAsync(CancellationToken.None);
        }
    }

    private static void AddVideoFile(MediaContext context, int episodeId)
    {
        context.VideoFiles.Add(
            new VideoFile
            {
                EpisodeId = episodeId,
                HostFolder = "season",
                Filename = $"episode-{episodeId}.mkv",
                Duration = "00:24:00",
            }
        );
    }

    private static EncodingCompletedEvent Completed(int episodeId) =>
        new()
        {
            JobId = episodeId,
            OutputPath = string.Empty,
            Duration = TimeSpan.Zero,
        };

    private sealed class CountingScope(IServiceScope inner, Action onDispose)
        : IServiceScope,
            IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose()
        {
            inner.Dispose();
            onDispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else
                inner.Dispose();
            onDispose();
        }
    }
}
