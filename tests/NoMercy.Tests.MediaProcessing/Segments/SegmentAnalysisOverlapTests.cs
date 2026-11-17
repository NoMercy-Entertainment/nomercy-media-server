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
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.SegmentAnalysis;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Jobs.SegmentJobs;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>Two runs for one season never overlap, and never leave two analysis rows of a kind.</summary>
[Trait("Category", "Unit")]
public class SegmentAnalysisOverlapTests
{
    [Fact]
    public async Task TwoJobsForOneSeason_RunOneAfterTheOther_OtherSeasonsInParallel()
    {
        int running = 0;
        int mostAtOnce = 0;
        Mock<ISegmentAnalysisService> analysis = new();
        analysis
            .Setup(a => a.AnalyseSeasonAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(
                async (int _, CancellationToken _) =>
                {
                    int now = Interlocked.Increment(ref running);
                    InterlockedMax(ref mostAtOnce, now);
                    await Task.Delay(150);
                    Interlocked.Decrement(ref running);
                    return SegmentAnalysisOutcome.Analysed;
                }
            );
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(analysis.Object)
            .AddSingleton(Mock.Of<ISegmentAnalysisState>())
            .AddSingleton<ISegmentAnalysisLocks, SegmentAnalysisLocks>()
            .BuildServiceProvider();

        await Task.WhenAll(
            Job(services, 10, "Hellsing, season 1").Handle(),
            Job(services, 10, "").Handle()
        );
        mostAtOnce
            .Should()
            .Be(1, "a scheduled and an on-demand job for one season wait for each other");

        mostAtOnce = 0;
        await Task.WhenAll(Job(services, 10, "").Handle(), Job(services, 11, "").Handle());
        mostAtOnce.Should().Be(2, "different seasons do not wait for each other");
    }

    [Fact]
    public async Task OverlappingWrites_LeaveOneAnalysisRowPerKind()
    {
        InMemorySegments repository = new();
        SegmentAnalysisLocks locks = new();
        ContentSegmentAnalysisWriter first = new(repository, locks);
        ContentSegmentAnalysisWriter second = new(repository, locks);
        AnalysedItem item = new(1, null, SegmentAnswers.Episode("1"));

        await Task.WhenAll(
            first.WriteAsync([item], CancellationToken.None),
            second.WriteAsync(
                [item with { Answer = SegmentAnswers.Episode("1") }],
                CancellationToken.None
            )
        );

        repository.Rows.Where(r => r.Source == "analysis").Should().HaveCount(2);
        repository.Rows.Count(r => r.SegmentType == ContentSegmentType.Intro).Should().Be(1);
        repository.Rows.Count(r => r.SegmentType == ContentSegmentType.Credits).Should().Be(1);
    }

    private static SegmentAnalysisJob Job(IServiceProvider services, int seasonId, string title)
    {
        SegmentAnalysisJob job = SegmentAnalysisJob.ForSeason(seasonId, title);
        job.InjectStorageServices(services);
        return job;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
                return;
        } while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }

    /// <summary>A segment table that is slow to read, so unguarded writers would both plan from an empty table.</summary>
    private sealed class InMemorySegments : IContentSegmentRepository
    {
        private readonly Lock _sync = new();

        public List<ContentSegment> Rows { get; } = [];

        public async Task<List<ContentSegment>> GetForEpisodesAsync(IReadOnlyList<int> episodeIds)
        {
            List<ContentSegment> snapshot;
            lock (_sync)
                snapshot = [.. Rows.Where(r => r.EpisodeId is { } id && episodeIds.Contains(id))];
            await Task.Delay(100);
            return snapshot;
        }

        public Task ApplyAsync(IReadOnlyList<ContentSegmentWritePlan> plans)
        {
            lock (_sync)
            {
                foreach (ContentSegmentWritePlan plan in plans)
                {
                    foreach (ContentSegment stale in plan.Remove)
                        Rows.RemoveAll(r => r.Id == stale.Id);
                    Rows.AddRange(plan.Add);
                }
            }

            return Task.CompletedTask;
        }

        public Task<List<ContentSegment>> GetForEpisodeAsync(int episodeId) =>
            throw new NotSupportedException();

        public Task<List<ContentSegment>> ListAsync(
            int pageSize,
            int pageIndex,
            ContentSegmentType? filterType = null
        ) => throw new NotSupportedException();

        public Task<int> GetTotalCountAsync(ContentSegmentType? filterType = null) =>
            throw new NotSupportedException();

        public Task<List<ContentSegment>> GetForMovieAsync(int movieId) =>
            Task.FromResult(new List<ContentSegment>());

        public Task<ContentSegment?> GetByIdAsync(Ulid id) => throw new NotSupportedException();

        public Task<ContentSegment> CreateAsync(ContentSegment segment) =>
            throw new NotSupportedException();

        public Task<ContentSegment?> UpdateAsync(Ulid id, Action<ContentSegment> apply) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(Ulid id) => throw new NotSupportedException();
    }
}
