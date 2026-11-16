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
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.Queue.TestHelpers;

public class TestJob : IShouldQueue
{
    public string QueueName => "default";
    public int Priority => 0;

    public string Message { get; set; } = string.Empty;
    public bool HasExecuted { get; set; }
    public bool ShouldFail { get; set; } = false;
    public int ExecutionDelay { get; set; } = 0;

    public async Task Handle()
    {
        if (ExecutionDelay > 0)
        {
            await Task.Delay(ExecutionDelay);
        }

        if (ShouldFail)
        {
            throw new InvalidOperationException($"TestJob failed with message: {Message}");
        }

        HasExecuted = true;
    }
}

public class AnotherTestJob : IShouldQueue
{
    public string QueueName => "default";
    public int Priority => 0;

    public int Value { get; set; }
    public bool HasExecuted { get; set; }

    public async Task Handle()
    {
        await Task.Delay(1); // Minimal delay to simulate work
        HasExecuted = true;
        Value *= 2;
    }
}

/// <summary>
/// A type that does NOT implement IShouldQueue — used to test the safety gate
/// that prevents non-job types from executing in the queue worker.
/// </summary>
public class NotAJob
{
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// A job whose <see cref="Handle"/> blocks on a test-controlled gate before
/// throwing a chosen exception type. Since a queue job round-trips through
/// JSON (losing any C# closure), the gate is looked up by a plain string key
/// that DOES survive serialization — the test signals the gate from outside
/// after observing the job has been reserved, letting it precisely race
/// QueueWorker's shutdown-cancellation against the job's own failure. Used to
/// prove the shutdown-vs-fault distinction in
/// <c>QueueWorker.StartAsync</c>'s catch clauses: the exact same exception
/// type is released for retry when the worker's stop token is already
/// cancelled, but fails/dead-letters normally when it isn't.
/// </summary>
public class GatedThrowingJob : IShouldQueue
{
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();

    public string QueueName => "gated";
    public int Priority => 0;

    public string GateKey { get; set; } = string.Empty;

    /// <summary>"cancelled" | "disposed" | "generic".</summary>
    public string ExceptionMode { get; set; } = "generic";

    public async Task Handle()
    {
        TaskCompletionSource gate = Gates.GetOrAdd(
            GateKey,
            _ => new(TaskCreationOptions.RunContinuationsAsynchronously)
        );
        await gate.Task;

        throw ExceptionMode switch
        {
            "cancelled" => new OperationCanceledException("test-cancelled-by-shutdown"),
            "canceled" => new OperationCanceledException("test-canceled-by-host-stop"),
            "disposed" => new ObjectDisposedException("test-scope-disposed-by-shutdown"),
            _ => new InvalidOperationException("test-generic-failure"),
        };
    }
}

/// <summary>
/// Proves N jobs are running AT THE SAME TIME rather than merely N jobs
/// eventually completing. Each instance signals "I have started" the moment
/// its <see cref="Handle"/> begins, then blocks on a shared release gate the
/// test controls. If fewer workers than jobs are actually active, later jobs
/// never get reserved (blocked behind the earlier ones still waiting on the
/// unreleased gate) and the start-countdown never completes — a real
/// concurrency deadlock the test observes as a timeout, not a guess.
/// </summary>
public class ConcurrencyProbeJob : IShouldQueue
{
    public static readonly ConcurrentDictionary<
        string,
        (CountdownEvent Started, ManualResetEventSlim Release)
    > Probes = new();

    public string QueueName => "gated";
    public int Priority => 0;

    public string ProbeKey { get; set; } = string.Empty;

    // JobQueue.Enqueue deduplicates by exact serialized payload — without a
    // per-instance discriminator, N probe jobs sharing the same ProbeKey
    // would serialize to byte-identical payloads and collapse into a single
    // enqueued row, silently invalidating any concurrency assertion built on
    // "N jobs enqueued".
    public string InstanceId { get; set; } = Guid.NewGuid().ToString();

    public Task Handle()
    {
        (CountdownEvent started, ManualResetEventSlim release) = Probes[ProbeKey];
        started.Signal();
        release.Wait(TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    }
}
