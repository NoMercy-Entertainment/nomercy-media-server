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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// Drives the real <see cref="QueueRunner"/> — not a reimplementation of its
/// pause/resume bookkeeping — against a real (in-process) configuration
/// store and real <see cref="NoMercyQueue.Workers.QueueWorker"/> instances,
/// asserting the actual effect: a paused queue does not process work, the
/// paused flag survives being read back from the store (the mechanism that
/// makes "pause survives a restart" true), and increasing a queue's worker
/// count actually raises the number of jobs it runs concurrently.
/// </summary>
[Trait("Category", "Unit")]
public class QueueRunnerBehaviorTests
{
    /// <summary>
    /// Real read-after-write semantics (not a mock) — Pause/Resume/Initialize
    /// all round-trip through this exactly like <c>MediaConfigurationStore</c>
    /// does against the DB.
    /// </summary>
    private sealed class InMemoryConfigurationStore : IConfigurationStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        private readonly TimeSpan _readDelay;

        public InMemoryConfigurationStore(TimeSpan readDelay = default)
        {
            _readDelay = readDelay;
        }

        public string? GetValue(string key)
        {
            if (_readDelay > TimeSpan.Zero)
                Thread.Sleep(_readDelay);
            return _values.GetValueOrDefault(key);
        }

        public void SetValue(string key, string value) => _values[key] = value;

        public Task SetValueAsync(string key, string value, Guid? modifiedBy = null)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public bool HasKey(string key) => _values.ContainsKey(key);
    }

    private static QueueRunner BuildRunner(
        TestQueueContextAdapter context,
        InMemoryConfigurationStore configStore,
        string queueName,
        int workerCount = 1
    )
    {
        QueueConfiguration configuration = new()
        {
            WorkerCounts = new() { [queueName] = workerCount },
        };
        return new(context, configuration, NullLoggerFactory.Instance, configStore);
    }

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

    [Fact]
    public async Task Pause_StopsWorker_JobIsNotReserved_AndIsPausedReflectsIt()
    {
        const string queue = "pause-behavior";
        TestQueueContextAdapter context = new();
        InMemoryConfigurationStore configStore = new();
        QueueRunner runner = BuildRunner(context, configStore, queue);
        await runner.Initialize();

        await runner.Pause(queue);

        runner.IsPaused(queue).Should().BeTrue();
        configStore.GetValue($"queue.{queue}.paused").Should().Be("true");

        runner.Queue.Enqueue(
            new QueueJobModel
            {
                Queue = queue,
                Payload = SerializationHelper.Serialize(
                    new TestJob { Message = "should stay queued" }
                ),
                AvailableAt = DateTime.UtcNow,
            }
        );

        await Task.Delay(300);
        context
            .Jobs.Should()
            .ContainSingle(
                j => j.ReservedAt == null,
                "a paused queue's worker must not reserve new work"
            );

        await runner.StopAll();
    }

    [Fact]
    public async Task Resume_ClearsPausedState_AndWorkerProcessesQueuedJob()
    {
        const string queue = "resume-behavior";
        TestQueueContextAdapter context = new();
        InMemoryConfigurationStore configStore = new();
        QueueRunner runner = BuildRunner(context, configStore, queue);
        await runner.Initialize();
        await runner.Pause(queue);

        runner.Queue.Enqueue(
            new QueueJobModel
            {
                Queue = queue,
                Payload = SerializationHelper.Serialize(
                    new TestJob { Message = "runs after resume" }
                ),
                AvailableAt = DateTime.UtcNow,
            }
        );

        await runner.Resume(queue);

        runner.IsPaused(queue).Should().BeFalse();
        configStore.GetValue($"queue.{queue}.paused").Should().Be("false");

        await WaitUntilAsync(
            () => context.Jobs.Count == 0,
            "resuming the queue must let its worker pick up and complete the queued job"
        );

        await runner.StopAll();
    }

    [Fact]
    public async Task Initialize_RestoresPersistedPause_QueueStaysPausedAcrossRestart()
    {
        const string queue = "restore-pause-behavior";
        TestQueueContextAdapter context = new();
        InMemoryConfigurationStore configStore = new();
        // Simulates the state left behind by a PREVIOUS QueueRunner instance
        // that was paused before the process exited.
        configStore.SetValue($"queue.{queue}.paused", "true");

        QueueRunner runner = BuildRunner(context, configStore, queue);
        await runner.Initialize();

        runner.IsPaused(queue).Should().BeTrue();

        runner.Queue.Enqueue(
            new QueueJobModel
            {
                Queue = queue,
                Payload = SerializationHelper.Serialize(new TestJob { Message = "must not run" }),
                AvailableAt = DateTime.UtcNow,
            }
        );

        await Task.Delay(300);
        context.Jobs.Should().ContainSingle(j => j.ReservedAt == null);

        await runner.StopAll();
    }

    [Fact]
    public async Task Initialize_PersistedPause_NeverReservesQueuedWorkAcross200Starts()
    {
        const string queue = "restore-pause-stress";
        for (int iteration = 0; iteration < 200; iteration++)
        {
            TestQueueContextAdapter context = new();
            InMemoryConfigurationStore configStore = new(TimeSpan.FromMilliseconds(20));
            configStore.SetValue($"queue.{queue}.paused", "true");
            QueueRunner runner = BuildRunner(context, configStore, queue, workerCount: 3);
            for (int jobIndex = 0; jobIndex < 3; jobIndex++)
            {
                runner.Queue.Enqueue(
                    new QueueJobModel
                    {
                        Queue = queue,
                        Payload = SerializationHelper.Serialize(
                            new TestJob { Message = $"must not run {jobIndex}" }
                        ),
                        AvailableAt = DateTime.UtcNow,
                    }
                );
            }

            try
            {
                await runner.Initialize();
                runner.IsPaused(queue).Should().BeTrue();
                context
                    .Jobs.Should()
                    .HaveCount(
                        3,
                        $"persisted pause must retain all queued work on startup {iteration + 1}"
                    );
                context
                    .Jobs.Should()
                    .OnlyContain(
                        job => job.ReservedAt == null && job.Attempts == 0,
                        $"no worker may reserve work on startup {iteration + 1}"
                    );
            }
            finally
            {
                await runner.StopAll();
            }
        }
    }

    [Fact]
    public async Task SetWorkerCount_UnknownQueueName_ReturnsFalse()
    {
        TestQueueContextAdapter context = new();
        InMemoryConfigurationStore configStore = new();
        QueueRunner runner = BuildRunner(context, configStore, "known-queue");
        await runner.Initialize();

        bool result = await runner.SetWorkerCount("no-such-queue", 5, null);

        result.Should().BeFalse();
        await runner.StopAll();
    }

    [Fact]
    public async Task SetWorkerCount_Increase_ActuallyRunsMoreJobsConcurrently()
    {
        const string queue = "concurrency-behavior";
        TestQueueContextAdapter context = new();
        InMemoryConfigurationStore configStore = new();
        // Start with a single worker — if SetWorkerCount's increase never
        // took effect, only one of the three barrier-waiting jobs below
        // would ever run and this test would time out.
        QueueRunner runner = BuildRunner(context, configStore, queue, workerCount: 1);
        await runner.Initialize();

        bool changed = await runner.SetWorkerCount(queue, 3, null);
        changed.Should().BeTrue();

        string probeKey = Guid.NewGuid().ToString();
        using CountdownEvent started = new(3);
        using ManualResetEventSlim release = new(false);
        ConcurrencyProbeJob.Probes[probeKey] = (started, release);

        for (int i = 0; i < 3; i++)
        {
            runner.Queue.Enqueue(
                new QueueJobModel
                {
                    Queue = queue,
                    Payload = SerializationHelper.Serialize(
                        new ConcurrencyProbeJob { ProbeKey = probeKey }
                    ),
                    AvailableAt = DateTime.UtcNow,
                }
            );
        }

        // If fewer than 3 workers are actually running, the later jobs stay
        // unreserved behind earlier ones still blocked on the unreleased
        // gate below — the start-countdown only reaches zero if 3 jobs are
        // genuinely running AT ONCE.
        bool allStarted = started.Wait(TimeSpan.FromSeconds(10));
        allStarted
            .Should()
            .BeTrue("increasing the worker count to 3 must let 3 jobs run concurrently");
        release.Set();

        await runner.StopAll();
    }
}
