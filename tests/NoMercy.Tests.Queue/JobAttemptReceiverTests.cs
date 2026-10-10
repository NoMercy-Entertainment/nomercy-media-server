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

using System.Collections.Concurrent;
using FluentAssertions;
using NoMercy.Database;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Workers;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// A job that wants to know whether a failure will be retried has to be told its
/// attempt by the worker, because the queue row is the only place it is counted.
/// </summary>
[Trait("Category", "Unit")]
public class JobAttemptReceiverTests : IDisposable
{
    private readonly QueueContext _context;
    private readonly IQueueContext _adapter;
    private readonly JobQueue _jobQueue;

    public JobAttemptReceiverTests()
    {
        (_context, _adapter) = TestQueueContextFactory.CreateInMemoryContextWithAdapter();
        _jobQueue = new(_adapter, maxAttempts: 3);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _context.Dispose();
    }

    [Fact]
    public async Task Worker_TellsAJobItsAttemptAndTheMaximum()
    {
        SemaphoreSlim ran = new(0, 1);
        string jobKey = Guid.NewGuid().ToString("N");
        AttemptRecordingJob.Configure(jobKey, () => ran.Release());

        _jobQueue.Enqueue(
            new()
            {
                Queue = "attempt-receiver",
                Payload = SerializationHelper.Serialize(
                    new AttemptRecordingJob { JobKey = jobKey }
                ),
                AvailableAt = DateTime.UtcNow,
            }
        );

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        QueueWorker worker = new(_jobQueue, "attempt-receiver");
        Task workerTask = worker.StartAsync(cts.Token);

        bool ranInTime = await ran.WaitAsync(TimeSpan.FromSeconds(8));
        worker.Stop();
        await cts.CancelAsync();
        await workerTask;

        ranInTime.Should().BeTrue("the job must run before the timeout");
        AttemptRecordingJob.Seen[jobKey].Should().Be((1, 3));
    }
}

public class AttemptRecordingJob : IShouldQueue, IJobAttemptReceiver
{
    public static readonly ConcurrentDictionary<string, (int Attempt, int Max)> Seen = new();
    private static readonly ConcurrentDictionary<string, Action> Callbacks = new();

    private int _attempt;
    private int _max;

    public string JobKey { get; set; } = string.Empty;
    public string QueueName => "attempt-receiver";
    public int Priority => 0;

    public static void Configure(string key, Action onRun) => Callbacks[key] = onRun;

    public void ReceiveAttempt(int attempt, int maxAttempts)
    {
        _attempt = attempt;
        _max = maxAttempts;
    }

    public Task Handle()
    {
        Seen[JobKey] = (_attempt, _max);
        if (Callbacks.TryGetValue(JobKey, out Action? onRun))
            onRun();
        return Task.CompletedTask;
    }

    public void Dispose() { }
}
