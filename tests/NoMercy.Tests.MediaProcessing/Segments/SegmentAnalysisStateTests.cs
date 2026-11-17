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
using NoMercy.Database;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>What the backfill remembers, in the Configuration table.</summary>
[Trait("Category", "Unit")]
public class SegmentAnalysisStateTests
{
    private readonly SegmentAnalysisState _state = new(new InMemoryAppFactory());

    [Fact]
    public async Task AnItemIsGivenUp_AfterThreeFailures()
    {
        for (int i = 1; i <= ISegmentAnalysisState.MaxFailures; i++)
        {
            (await _state.GivenUpAsync(SegmentAnalysisTarget.Season, default))
                .Should()
                .NotContain(10);
            (await _state.RecordFailureAsync(SegmentAnalysisTarget.Season, 10, default))
                .Should()
                .Be(i);
        }

        (await _state.GivenUpAsync(SegmentAnalysisTarget.Season, default)).Should().Equal(10);
        (await _state.GivenUpAsync(SegmentAnalysisTarget.Movie, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewEncodeOrAManualRun_ForgetsTheFailures()
    {
        for (int i = 0; i < ISegmentAnalysisState.MaxFailures; i++)
            await _state.RecordFailureAsync(SegmentAnalysisTarget.Movie, 129, default);

        await _state.ResetFailuresAsync(SegmentAnalysisTarget.Movie, 129, default);

        (await _state.GivenUpAsync(SegmentAnalysisTarget.Movie, default)).Should().BeEmpty();
        (await _state.RecordFailureAsync(SegmentAnalysisTarget.Movie, 129, default)).Should().Be(1);
    }

    [Fact]
    public async Task Analysed_IsRemembered_AndClearsFailures()
    {
        await _state.RecordFailureAsync(SegmentAnalysisTarget.Season, 11, default);
        await _state.MarkAnalysedAsync(SegmentAnalysisTarget.Season, 11, default);
        await _state.MarkAnalysedAsync(SegmentAnalysisTarget.Season, 3, default);

        (await _state.AnalysedAsync(SegmentAnalysisTarget.Season, default))
            .Should()
            .BeEquivalentTo([3, 11]);
        (await _state.RecordFailureAsync(SegmentAnalysisTarget.Season, 11, default)).Should().Be(1);
    }

    [Fact]
    public void StoredValues_AreReadTolerantly()
    {
        SegmentAnalysisState.Parse("3, 12,x,40").Should().BeEquivalentTo([3, 12, 40]);
        SegmentAnalysisState
            .ParseCounts("10:2,bad,11:x,12:3")
            .Should()
            .BeEquivalentTo(new Dictionary<int, int> { [10] = 2, [12] = 3 });
    }

    private sealed class InMemoryAppFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        public AppDbContext CreateDbContext() => new(_options);
    }
}
