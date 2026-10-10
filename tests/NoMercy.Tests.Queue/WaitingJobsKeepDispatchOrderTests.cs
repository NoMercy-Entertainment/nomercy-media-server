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
using NoMercy.Database;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// A season dispatched E01…E13 must encode in that order.
/// <para>
/// The reserve query orders by priority then position, but it only considers
/// rows whose <c>AvailableAt</c> has passed — so the sleep a waiting coordinator
/// chooses is what actually decides which job runs next. While that sleep
/// carried ±5s of random jitter, each wake-up wrote a different offset onto
/// every row, one job was eligible at a time, and which one was a dice roll:
/// the ordering never got to decide anything. These tests pin the property the
/// ordering depends on — waiting jobs share a wake-up time, so position breaks
/// the tie.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class WaitingJobsKeepDispatchOrderTests : IDisposable
{
    private readonly QueueContext _context;
    private readonly IQueueContext _adapter;

    public WaitingJobsKeepDispatchOrderTests()
    {
        (_context, _adapter) = TestQueueContextFactory.CreateInMemoryContextWithAdapter();
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _context.Dispose();
    }

    private void Enqueue(string payload, DateTime availableAt)
    {
        _context.QueueJobs.Add(
            new()
            {
                Queue = "encoder",
                Payload = payload,
                Priority = 4,
                Attempts = 0,
                AvailableAt = availableAt,
                CreatedAt = new(2026, 7, 27, 23, 10, 21, DateTimeKind.Utc),
            }
        );
        _context.SaveChanges();
    }

    private List<string> ReserveAll(int count)
    {
        JobQueue jobQueue = new(_adapter);
        List<string> taken = [];
        for (int i = 0; i < count; i++)
        {
            QueueJobModel? job = jobQueue.ReserveJob("encoder", null);
            if (job is null)
                break;
            taken.Add(job.Payload);
            _adapter.RemoveJob(job);
        }
        return taken;
    }

    [Fact]
    public void Waiting_jobs_that_share_a_wake_up_time_run_in_dispatch_order()
    {
        DateTime wakeUp = DateTime.UtcNow.AddSeconds(-1);
        foreach (string episode in new[] { "E01", "E02", "E03", "E04", "E05" })
            Enqueue(episode, wakeUp);

        ReserveAll(5).Should().Equal("E01", "E02", "E03", "E04", "E05");
    }

    [Fact]
    public void A_job_whose_wake_up_lands_first_runs_first_whatever_its_position()
    {
        // The mechanism the jitter fed. Staggered wake-ups do not reorder jobs
        // that are all already awake — position still breaks that tie. They
        // reorder because they are in the FUTURE and expire one at a time: at
        // any moment exactly one job is eligible, so the reserve has no choice
        // to make and the ordering is decided entirely by whose offset was
        // smallest. Here E02 runs while E01 is still asleep.
        DateTime now = DateTime.UtcNow;
        Enqueue("E01", now.AddSeconds(30));
        Enqueue("E02", now.AddSeconds(-1));
        Enqueue("E03", now.AddSeconds(30));

        ReserveAll(3).Should().Equal("E02");
    }

    [Fact]
    public void Priority_still_outranks_position()
    {
        DateTime wakeUp = DateTime.UtcNow.AddSeconds(-1);
        Enqueue("E01", wakeUp);
        _context.QueueJobs.Add(
            new()
            {
                Queue = "encoder",
                Payload = "urgent",
                Priority = 9,
                Attempts = 0,
                AvailableAt = wakeUp,
                CreatedAt = new(2026, 7, 27, 23, 10, 22, DateTimeKind.Utc),
            }
        );
        _context.SaveChanges();

        ReserveAll(2).Should().Equal("urgent", "E01");
    }
}
