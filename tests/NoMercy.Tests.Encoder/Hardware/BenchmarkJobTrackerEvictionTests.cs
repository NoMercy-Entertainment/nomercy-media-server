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

using NoMercy.Encoder.Hardware;

namespace NoMercy.Tests.Encoder.Hardware;

/// <summary>
/// BenchmarkJobTracker._jobs was never pruned, so repeated Start() calls over a
/// long uptime grew it without bound. EvictionCandidates decides what to drop.
/// </summary>
public class BenchmarkJobTrackerEvictionTests
{
    private static BenchmarkJobStatus Job(string id, DateTime? completedAt) =>
        new(
            JobId: id,
            Status: completedAt is null ? "running" : "completed",
            StartedAt: DateTime.UtcNow,
            CompletedAt: completedAt,
            MeasurementCount: 0,
            RequestedCodecs: [],
            RequestedResolutions: [],
            Error: null
        );

    [Fact]
    public void EvictionCandidates_AtOrUnderCap_EvictsNothing()
    {
        List<BenchmarkJobStatus> jobs = [Job("a", DateTime.UtcNow), Job("b", DateTime.UtcNow)];

        Assert.Empty(BenchmarkJobTracker.EvictionCandidates(jobs, 100));
    }

    [Fact]
    public void EvictionCandidates_OverCap_DropsOldestCompletedFirst()
    {
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<BenchmarkJobStatus> jobs = [];
        for (int i = 0; i < 105; i++)
            jobs.Add(Job($"job{i:D3}", baseTime.AddMinutes(i)));

        List<string> evicted = BenchmarkJobTracker.EvictionCandidates(jobs, 100).ToList();

        Assert.Equal(5, evicted.Count);
        Assert.Equal(["job000", "job001", "job002", "job003", "job004"], evicted);
    }

    [Fact]
    public void EvictionCandidates_NeverEvictsRunningJobs()
    {
        DateTime baseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        List<BenchmarkJobStatus> jobs = [];
        for (int i = 0; i < 96; i++)
            jobs.Add(Job($"done{i:D3}", baseTime.AddMinutes(i)));
        for (int i = 0; i < 10; i++)
            jobs.Add(Job($"run{i:D2}", null));

        List<string> evicted = BenchmarkJobTracker.EvictionCandidates(jobs, 100).ToList();

        Assert.Equal(6, evicted.Count);
        Assert.All(evicted, id => Assert.StartsWith("done", id));
    }
}
