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
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Workers;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// A normal host stop never calls <see cref="QueueWorker.Stop"/>: the worker
/// only learns of it through the host-stopping token. A job that rethrows its
/// cancellation then must be released, not failed, or every nightly restart
/// spends one attempt of each running encode.
/// </summary>
[Trait("Category", "Unit")]
public class QueueWorkerHostStopTests
{
    private static async Task WaitUntilAsync(Func<bool> predicate, string failureMessage)
    {
        using CancellationTokenSource cts = new(QueueTestTiming.WaitWindow);
        while (!cts.IsCancellationRequested)
        {
            if (predicate())
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException(failureMessage);
    }

    private static (
        QueueWorker Worker,
        TestQueueContextAdapter Context,
        string Payload,
        string GateKey
    ) Build(string queueName, string exceptionMode, CancellationToken hostStopping)
    {
        TestQueueContextAdapter context = new();
        JobQueue jobQueue = new(context);
        string gateKey = Guid.NewGuid().ToString();
        GatedThrowingJob.Gates[gateKey] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string payload = SerializationHelper.Serialize(
            new GatedThrowingJob { GateKey = gateKey, ExceptionMode = exceptionMode }
        );
        context.AddJob(
            new QueueJobModel
            {
                Queue = queueName,
                Payload = payload,
                AvailableAt = DateTime.UtcNow,
            }
        );
        QueueWorker worker = new(jobQueue, queueName, hostStopping: hostStopping);
        return (worker, context, payload, gateKey);
    }

    private static async Task WaitUntilReservedAsync(
        TestQueueContextAdapter context,
        string payload
    )
    {
        await WaitUntilAsync(
            () => context.Jobs.Any(j => j.Payload == payload && j.ReservedAt != null),
            "Job was never reserved within the wait window."
        );
    }

    [Theory]
    [InlineData("canceled")]
    [InlineData("disposed")]
    public async Task HostStop_WithoutWorkerStop_ReleasesTheJob_NeverFailsIt(string exceptionMode)
    {
        using CancellationTokenSource hostStopping = new();
        (QueueWorker worker, TestQueueContextAdapter context, string payload, string gateKey) =
            Build($"host-stop-{exceptionMode}", exceptionMode, hostStopping.Token);
        worker.Start();

        await WaitUntilReservedAsync(context, payload);
        // The host cancels its own token; worker.Stop() is never called.
        hostStopping.Cancel();
        GatedThrowingJob.Gates[gateKey].SetResult();

        await WaitUntilAsync(
            () => context.Jobs.Any(j => j.Payload == payload && j.ReservedAt == null),
            "A job cut short by a host stop was never released back to the queue."
        );

        QueueJobModel survivor = context.Jobs.Single(j => j.Payload == payload);
        survivor
            .Attempts.Should()
            .Be(0, "a host stop must not spend an attempt of the interrupted job");
        context.FailedJobs.Should().BeEmpty();
    }

    [Fact]
    public async Task OperationCanceled_WhileHostIsRunning_StillSpendsAnAttempt()
    {
        using CancellationTokenSource hostStopping = new();
        (QueueWorker worker, TestQueueContextAdapter context, string payload, string gateKey) =
            Build("host-running-cancel", "canceled", hostStopping.Token);
        worker.Start();

        await WaitUntilReservedAsync(context, payload);
        GatedThrowingJob.Gates[gateKey].SetResult();

        await WaitUntilAsync(
            () => context.FailedJobs.Any(f => f.Payload == payload),
            "A cancellation with no host stop and no worker stop must still dead-letter."
        );

        context.Jobs.Should().NotContain(j => j.Payload == payload);
        worker.Stop();
    }
}
