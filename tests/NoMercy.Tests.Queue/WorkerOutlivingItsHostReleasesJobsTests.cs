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
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Workers;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// A worker whose service provider has been disposed cannot run anything ever again,
/// so the jobs it holds belong to somebody else — not in FailedJobs.
/// <para>
/// First boot serves setup over plain HTTP, then throws that host away and builds a
/// second one for HTTPS. The queue outlives both. The first host's workers were never
/// signalled to stop, so they kept reserving jobs and scoping them against a container
/// that no longer existed; every one threw <see cref="ObjectDisposedException"/> out of
/// <c>IServiceScopeFactory.CreateScope</c>. The catch that handles exactly this
/// required the worker's stop token to be cancelled, which for those workers it never
/// was, so each job fell through to <c>FailJob</c> and was dead-lettered while the new
/// host's workers were running fine alongside them.
/// </para>
/// <para>
/// Measured on a real first boot (2026-08-02): 367 dead-lettered jobs, a library added
/// after the restart whose scan never ran, and 9 of 17 films imported. The user sees a
/// half-populated library and an empty one, with nothing on screen to explain either.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class WorkerOutlivingItsHostReleasesJobsTests : IDisposable
{
    private readonly QueueContext _context;
    private readonly IQueueContext _adapter;

    public WorkerOutlivingItsHostReleasesJobsTests()
    {
        (_context, _adapter) = TestQueueContextFactory.CreateInMemoryContextWithAdapter();
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A scope factory backed by a container that has already been disposed — the exact
    /// shape left behind when the HTTP host goes away while its workers keep polling.
    /// </summary>
    private static IServiceScopeFactory DisposedScopeFactory()
    {
        ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        IServiceScopeFactory factory = provider.GetRequiredService<IServiceScopeFactory>();
        provider.Dispose();

        return factory;
    }

    private JobQueue BuildQueue() => new(_adapter);

    private void Enqueue(string queueName)
    {
        _context.QueueJobs.Add(
            new()
            {
                Queue = queueName,
                // A genuinely runnable job — the point is that a healthy worker could
                // have completed it, so losing it is a real loss and not a bad payload.
                Payload = SerializationHelper.Serialize(new TestJob { Message = "scan" }),
                Priority = 0,
                CreatedAt = DateTime.UtcNow,
                AvailableAt = DateTime.UtcNow,
            }
        );
        _context.SaveChanges();
    }

    [Fact]
    public async Task ADisposedProvider_ReleasesTheJobInsteadOfDeadLetteringIt()
    {
        const string queueName = "library";
        Enqueue(queueName);

        JobQueue queue = BuildQueue();
        QueueWorker worker = new(queue, queueName, scopeFactory: DisposedScopeFactory());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        // The worker must exit on its own when its provider is gone — a stop signal is
        // exactly what it does not get in the real failure.
        await worker.StartAsync(cts.Token);

        _context.FailedJobs.Should().BeEmpty("a dead provider is not the job's fault");
        _context
            .QueueJobs.Should()
            .ContainSingle("the job must stay queued for a worker that can still run it");
        _context.QueueJobs.Single().ReservedAt.Should().BeNull("the reservation is released");
    }
}
