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

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Data.Plugins;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// A RoFormer run is judged by the filter's chunk lines: five minutes without
/// one ends it, the clock running from the start so the model load counts,
/// and the last chunk hands the run back to its own limits. On a fake clock,
/// with lines recorded from the real filter.
/// </summary>
public sealed class StemsplitProgressWatchTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new();
    private readonly StemsplitProgressWatch _watch;

    public StemsplitProgressWatchTests()
    {
        _watch = new(StemsplitProgressWatch.DefaultNoChunkLimit, _clock);
    }

    public void Dispose() => _watch.Dispose();

    [Fact]
    public void TheLimit_IsFiveMinutes()
    {
        StemsplitProgressWatch.DefaultNoChunkLimit.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ARunThatNeverHandsOutAChunk_StallsAfterTheLimit_CountedFromTheStart()
    {
        _watch.Start();
        _watch.Observe(StemsplitLogFixtures.ModelLoaded);

        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        _watch.HasStalled.Should().BeFalse();

        _clock.Advance(TimeSpan.FromSeconds(1));
        _watch.HasStalled.Should().BeTrue();
    }

    [Fact]
    public void EachChunkLine_RestartsTheClock_OtherLinesDoNot()
    {
        _watch.Start();

        for (int minute = 0; minute < 20; minute++)
        {
            _clock.Advance(TimeSpan.FromMinutes(4));
            _watch.Observe(StemsplitLogFixtures.ChunkAfterStats);
        }

        _watch.HasStalled.Should().BeFalse();

        _clock.Advance(TimeSpan.FromMinutes(4));
        _watch.Observe(StemsplitLogFixtures.RunsOn);
        _clock.Advance(TimeSpan.FromMinutes(1));

        _watch.HasStalled.Should().BeTrue();
    }

    [Fact]
    public void TheLastChunk_StopsTheClock_AndSaysTheSplitIsDone()
    {
        bool handedBack = false;
        using CancellationTokenRegistration registration = _watch.SplitDone.Register(() =>
            handedBack = true
        );
        _watch.Start();

        _watch.Observe(StemsplitLogFixtures.FirstChunk);
        _watch.Observe(StemsplitLogFixtures.LastChunkOfFirstModel);
        _watch.IsSplitDone.Should().BeFalse();

        _watch.Observe(StemsplitLogFixtures.LastChunk);
        _clock.Advance(TimeSpan.FromHours(1));

        _watch.IsSplitDone.Should().BeTrue();
        handedBack.Should().BeTrue();
        _watch.HasStalled.Should().BeFalse();
    }

    /// <summary>
    /// Two RoFormer filters in one graph, as the real binary prints them: the
    /// first one's last chunk leaves the second still splitting, and the run
    /// is still judged by chunk lines until the second is done too.
    /// </summary>
    [Fact]
    public void WithTwoFilters_TheSplitIsDoneOnlyWhenBothHandedOutTheirLastChunk()
    {
        using StemsplitProgressWatch watch = new(TimeSpan.FromMinutes(5), _clock, filters: 2);
        watch.Start();

        foreach (string line in StemsplitLogFixtures.TwoFilters[..8])
            watch.Observe(line);

        watch.IsSplitDone.Should().BeFalse("Parsed_stemsplit_2 is still splitting");
        _clock.Advance(TimeSpan.FromMinutes(5));
        watch.HasStalled.Should().BeTrue("the second filter's silence is still a stall");
    }

    [Fact]
    public void WithTwoFilters_BothLastChunks_FinishTheSplit()
    {
        using StemsplitProgressWatch watch = new(TimeSpan.FromMinutes(5), _clock, filters: 2);
        watch.Start();

        foreach (string line in StemsplitLogFixtures.TwoFilters)
            watch.Observe(line);

        watch.IsSplitDone.Should().BeTrue();
        _clock.Advance(TimeSpan.FromHours(1));
        watch.HasStalled.Should().BeFalse();
    }

    [Fact]
    public void OneFiltersLastChunkTwice_IsStillOneFilterDone()
    {
        using StemsplitProgressWatch watch = new(TimeSpan.FromMinutes(5), _clock, filters: 2);
        watch.Start();

        watch.Observe(StemsplitLogFixtures.TwoFilters[7]);
        watch.Observe(StemsplitLogFixtures.TwoFilters[7]);

        watch.IsSplitDone.Should().BeFalse();
    }

    [Fact]
    public void ALineAfterDisposal_IsIgnored()
    {
        StemsplitProgressWatch watch = new(TimeSpan.FromMinutes(5), _clock);
        watch.Start();
        watch.Dispose();

        Action late = () => watch.Observe(StemsplitLogFixtures.FirstChunk);

        late.Should().NotThrow();
    }
}
