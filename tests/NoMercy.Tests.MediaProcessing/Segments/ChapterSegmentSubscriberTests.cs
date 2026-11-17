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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.MediaProcessing.EventHandlers;
using NoMercy.MediaProcessing.Jobs.SegmentJobs;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

[Trait("Category", "Unit")]
public class ChapterSegmentSubscriberTests
{
    private readonly Mock<IChapterSegmentWriter> _writer = new();

    [Fact]
    public async Task An_encoded_episode_is_classified()
    {
        await Subscriber()
            .OnEncodingCompletedAsync(Completed(episodeId: 62085), CancellationToken.None);

        _writer.Verify(
            w => w.ClassifyEpisodeAsync(62085, It.IsAny<CancellationToken>()),
            Times.Once
        );
        _writer.Verify(
            w => w.ClassifyMovieAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task An_encoded_movie_is_classified()
    {
        await Subscriber()
            .OnEncodingCompletedAsync(Completed(movieId: 129), CancellationToken.None);

        _writer.Verify(w => w.ClassifyMovieAsync(129, It.IsAny<CancellationToken>()), Times.Once);
        _writer.Verify(
            w => w.ClassifyEpisodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task An_encode_that_names_no_episode_or_movie_is_ignored()
    {
        await Subscriber().OnEncodingCompletedAsync(Completed(), CancellationToken.None);

        _writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_failure_is_logged_and_never_fails_the_encode()
    {
        _writer
            .Setup(w => w.ClassifyEpisodeAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        Func<Task> act = () =>
            Subscriber().OnEncodingCompletedAsync(Completed(episodeId: 1), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_subscriber_listens_for_completed_encodes_while_running()
    {
        Mock<IEventBus> bus = new();
        Mock<IDisposable> subscription = new();
        bus.Setup(b =>
                b.Subscribe(It.IsAny<Func<EncodingCompletedEvent, CancellationToken, Task>>())
            )
            .Returns(subscription.Object);
        ChapterSegmentSubscriber subscriber = new(
            bus.Object,
            _writer.Object,
            NullLogger<ChapterSegmentSubscriber>.Instance
        );

        await subscriber.StartAsync(CancellationToken.None);
        await subscriber.StopAsync(CancellationToken.None);

        bus.Verify(
            b => b.Subscribe(It.IsAny<Func<EncodingCompletedEvent, CancellationToken, Task>>()),
            Times.Once
        );
        subscription.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public async Task The_daily_sweep_classifies_everything()
    {
        ChapterSegmentSweepCronJob job = new(
            _writer.Object,
            NullLogger<ChapterSegmentSweepCronJob>.Instance
        );

        await job.ExecuteAsync(string.Empty, CancellationToken.None);

        _writer.Verify(w => w.ClassifyAllAsync(It.IsAny<CancellationToken>()), Times.Once);
        job.CronExpression.Should().NotBeNullOrWhiteSpace();
    }

    private ChapterSegmentSubscriber Subscriber() =>
        new(Mock.Of<IEventBus>(), _writer.Object, NullLogger<ChapterSegmentSubscriber>.Instance);

    private static EncodingCompletedEvent Completed(int? episodeId = null, int? movieId = null) =>
        new()
        {
            JobId = episodeId ?? movieId ?? 7,
            OutputPath = "/out",
            Duration = TimeSpan.Zero,
            EpisodeId = episodeId,
            MovieId = movieId,
        };
}
