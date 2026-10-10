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

using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Queue;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Resources;
using NoMercyQueue.Workers;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// Regression net for the field bug "encoder-gpu queue permanently jammed":
/// a job pinned to a GPU device key that is not (and will never be)
/// registered on this host — e.g. an <c>h264_amf</c> requirement on a host
/// whose only GPU is NVIDIA — must degrade to software and reroute to the
/// CPU queue instead of looping at the budget gate forever. A job whose GPU
/// IS registered but merely busy right now must keep retrying exactly as
/// before — only an absent device triggers the degrade path.
/// </summary>
public class BudgetGateDegradeTests : IDisposable
{
    private readonly QueueContext _context;
    private readonly IQueueContext _adapter;
    private readonly JobQueue _jobQueue;

    public BudgetGateDegradeTests()
    {
        (_context, _adapter) = TestQueueContextFactory.CreateInMemoryContextWithAdapter();
        _jobQueue = new(_adapter);
    }

    public void Dispose()
    {
        _adapter.Dispose();
        _context.Dispose();
    }

    [Fact]
    public async Task AbsentGpuDevice_DegradesToSoftware_AndReroutesToCpuQueue()
    {
        Mock<IResourceBudget> budget = new(MockBehavior.Loose);

        // TryAcquire returns null for every attempt — from the worker's
        // perspective this looks identical to ordinary saturation...
        budget
            .Setup(b => b.TryAcquire(It.IsAny<ResourceRequirement>(), TimeSpan.Zero))
            .Returns((ResourceLease?)null);

        // ...until the worker asks whether the GPU key is even registered.
        // It never is — there is no AMD device on this host.
        budget.Setup(b => b.IsGpuDeviceRegistered("h264_amf")).Returns(false);

        DegradableResourceRequirementJob job = new()
        {
            QueueName = "encoder-gpu",
            ResourceRequirement = new("h264_amf", GpuSlots: 1, CpuThreads: 2),
        };

        QueueJob queueJob = new()
        {
            Queue = "encoder-gpu",
            Payload = SerializationHelper.Serialize(job),
            AvailableAt = DateTime.UtcNow,
            Attempts = 0,
        };

        _context.QueueJobs.Add(queueJob);
        await _context.SaveChangesAsync();

        QueueWorker worker = new(
            _jobQueue,
            "encoder-gpu",
            resourceBudget: budget.Object,
            resourceAwareQueues: new HashSet<string> { "encoder-gpu", "encoder-cpu" }
        );

        using CancellationTokenSource cts = new(QueueTestTiming.WaitWindow);

        try
        {
            await worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected once the cancellation token fires
        }

        // The job must still exist — degraded and rerouted, not dropped.
        QueueJob persisted = Assert.Single(_context.QueueJobs);
        Assert.Equal("encoder-cpu", persisted.Queue);
        Assert.Null(persisted.ReservedAt);

        DegradableResourceRequirementJob degraded = (DegradableResourceRequirementJob)
            SerializationHelper.Deserialize<object>(persisted.Payload);
        Assert.Null(degraded.ResourceRequirement?.GpuDeviceKey);
        Assert.Equal("encoder-cpu", degraded.QueueName);

        // The absent-device path never reaches TryAcquire's granted branch,
        // so no lease is ever handed back to release.
        budget.Verify(b => b.Release(It.IsAny<ResourceLease>()), Times.Never);
    }

    [Fact]
    public async Task PresentButBusyGpuDevice_StillRetries_DoesNotDegrade()
    {
        Mock<IResourceBudget> budget = new(MockBehavior.Loose);

        // Saturated, not absent: TryAcquire returns null but the device IS
        // registered — every slot is just currently leased.
        budget
            .Setup(b => b.TryAcquire(It.IsAny<ResourceRequirement>(), TimeSpan.Zero))
            .Returns((ResourceLease?)null);
        budget.Setup(b => b.IsGpuDeviceRegistered("test-gpu")).Returns(true);
        budget.Setup(b => b.AvailableGpuEncoderSlots("test-gpu")).Returns(0);
        budget.Setup(b => b.AvailableCpuThreads()).Returns(4);

        DegradableResourceRequirementJob job = new()
        {
            QueueName = "encoder-gpu",
            ResourceRequirement = new("test-gpu", GpuSlots: 1, CpuThreads: 2),
        };

        QueueJob queueJob = new()
        {
            Queue = "encoder-gpu",
            Payload = SerializationHelper.Serialize(job),
            AvailableAt = DateTime.UtcNow,
            Attempts = 0,
        };

        _context.QueueJobs.Add(queueJob);
        await _context.SaveChangesAsync();

        QueueWorker worker = new(
            _jobQueue,
            "encoder-gpu",
            resourceBudget: budget.Object,
            resourceAwareQueues: new HashSet<string> { "encoder-gpu", "encoder-cpu" }
        );

        // Cancel before the 5s BudgetRetryDelay fully elapses again — one
        // saturated pass through the gate is enough to prove the branch taken.
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        try
        {
            await worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // The job stays on its ORIGINAL queue — no degrade, no reroute.
        QueueJob persisted = Assert.Single(_context.QueueJobs);
        Assert.Equal("encoder-gpu", persisted.Queue);

        DegradableResourceRequirementJob stillPinned = (DegradableResourceRequirementJob)
            SerializationHelper.Deserialize<object>(persisted.Payload);
        Assert.Equal("test-gpu", stillPinned.ResourceRequirement?.GpuDeviceKey);

        // DegradeToSoftware must never have been invoked for a present device.
        Assert.False(stillPinned.DegradeCalled);
    }
}

/// <summary>
/// Test job that implements <see cref="IResourceDegradable"/> alongside
/// <see cref="IHasResourceRequirement"/> so the budget gate's absent-device
/// path can be exercised end to end. Placed in the
/// <c>NoMercy.Tests.Queue</c> namespace so the serialization binder (which
/// allows <c>NoMercy.*</c> types) can round-trip the payload.
/// </summary>
public class DegradableResourceRequirementJob
    : IShouldQueue,
        IHasResourceRequirement,
        IResourceDegradable
{
    public string QueueName { get; set; } = string.Empty;
    public int Priority => 1;

    public ResourceRequirement? ResourceRequirement { get; set; }

    public bool HasExecuted { get; private set; }

    /// <summary>
    /// Set on THIS instance when <see cref="DegradeToSoftware"/> runs.
    /// Deserialized copies always start false — used to assert the present-
    /// but-busy test path never called it on the persisted payload.
    /// </summary>
    public bool DegradeCalled { get; private set; }

    public Task Handle()
    {
        HasExecuted = true;
        return Task.CompletedTask;
    }

    public IShouldQueue? DegradeToSoftware()
    {
        DegradeCalled = true;

        if (ResourceRequirement?.GpuDeviceKey is null)
            return null;

        return new DegradableResourceRequirementJob
        {
            QueueName = "encoder-cpu",
            ResourceRequirement = ResourceRequirement with { GpuDeviceKey = null, GpuSlots = 0 },
        };
    }
}
