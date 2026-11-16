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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Queue.MediaServer;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// Checkpoint-aware orphan triage (Phase 4.14b).
///
/// When <see cref="IOrphanCheckpointLookup"/> is registered, encoder-queue
/// orphans with a crash checkpoint must be re-queued with Attempts=0 instead
/// of being moved to FailedJobs. Orphans without a checkpoint keep the
/// non-checkpoint recovery behaviour: a repeatedly-failing orphan
/// (Attempts &gt; 1) dead-letters regardless of queue.
/// </summary>
public class OrphanJobRecoveryCheckpointTests
{
    private const string EncoderQueue = "encoder";
    private const string EncoderGpuQueue = "encoder-gpu";
    private const string LibraryQueue = "library";

    private static (
        OrphanJobRecoveryHostedService Service,
        TestQueueContextAdapter Context
    ) BuildService(IOrphanCheckpointLookup? lookup = null)
    {
        TestQueueContextAdapter context = new();
        ServiceCollection services = new();
        services.AddSingleton<IQueueContext>(context);
        if (lookup is not null)
            services.AddSingleton<IOrphanCheckpointLookup>(lookup);
        ServiceProvider provider = services.BuildServiceProvider();
        OrphanJobRecoveryHostedService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OrphanJobRecoveryHostedService>.Instance
        );
        return (service, context);
    }

    // ── With checkpoint present ─────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_EncoderOrphanWithCheckpoint_ReQueuedWithZeroAttempts()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 2,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        // Job stays in queue with Attempts=0, ready for resume.
        Assert.Single(context.Jobs);
        Assert.Empty(context.FailedJobs);
        Assert.Equal(0, context.Jobs[0].Attempts);
        Assert.Null(context.Jobs[0].ReservedAt);
    }

    [Fact]
    public async Task StartAsync_EncoderGpuOrphanWithCheckpoint_ReQueuedWithZeroAttempts()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderGpuQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 1,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        Assert.Single(context.Jobs);
        Assert.Empty(context.FailedJobs);
        Assert.Equal(0, context.Jobs[0].Attempts);
    }

    [Fact]
    public async Task StartAsync_EncoderOrphanWithCheckpointBelowTheCap_ResumedAndChargedOneInterruption()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 1,
            Interruptions = OrphanRecoveryTriage.MaxInterruptions - 2,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        Assert.Single(context.Jobs);
        Assert.Empty(context.FailedJobs);
        Assert.Equal(0, context.Jobs[0].Attempts);
        Assert.Null(context.Jobs[0].ReservedAt);
        Assert.Equal(OrphanRecoveryTriage.MaxInterruptions - 1, context.Jobs[0].Interruptions);
    }

    [Fact]
    public async Task StartAsync_EncoderOrphanWithCheckpointInterruptedTooOften_MovedToFailedJobs()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 1,
            Interruptions = OrphanRecoveryTriage.MaxInterruptions - 1,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        Assert.Empty(context.Jobs);
        Assert.Single(context.FailedJobs);
    }

    // ── Without checkpoint — original behaviour preserved ──────────────────

    [Fact]
    public async Task StartAsync_EncoderOrphanInterruptedTooOftenNoCheckpoint_MovedToFailedJobs()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 2,
            Interruptions = OrphanRecoveryTriage.MaxInterruptions - 1,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        Assert.Empty(context.Jobs);
        Assert.Single(context.FailedJobs);
        Assert.Equal("job.interrupted_no_checkpoint", context.FailedJobs[0].Exception);
    }

    // ── Non-encoder queue — checkpoint lookup not consulted ────────────────

    [Fact]
    public async Task StartAsync_NonEncoderOrphanInterruptedTooOften_MovedToFailedJobs_LookupNotCalled()
    {
        Mock<IOrphanCheckpointLookup> lookup = new();
        lookup
            .Setup(l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup.Object
        );

        QueueJobModel orphan = new()
        {
            Queue = LibraryQueue,
            Payload = "{\"Id\":\"job-lib\"}",
            Priority = 5,
            Attempts = 2,
            Interruptions = OrphanRecoveryTriage.MaxInterruptions - 1,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        // A repeatedly-failing (Attempts > 1) library orphan always dead-letters — lookup is not consulted.
        Assert.Empty(context.Jobs);
        Assert.Single(context.FailedJobs);
        lookup.Verify(
            l => l.HasCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    // ── No IOrphanCheckpointLookup registered — original behaviour ──────────

    [Fact]
    public async Task StartAsync_NoLookupRegistered_EncoderOrphanInterruptedTooOften_MovedToFailed()
    {
        (OrphanJobRecoveryHostedService service, TestQueueContextAdapter context) = BuildService(
            lookup: null
        );

        QueueJobModel orphan = new()
        {
            Queue = EncoderQueue,
            Payload = "{\"OutputDirectory\":\"/media/output\"}",
            Priority = 5,
            Attempts = 2,
            Interruptions = OrphanRecoveryTriage.MaxInterruptions - 1,
            ReservedAt = DateTime.UtcNow.AddMinutes(-5),
            AvailableAt = DateTime.UtcNow.AddHours(-1),
        };
        context.AddJob(orphan);

        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
            await service.ExecuteTask;

        Assert.Empty(context.Jobs);
        Assert.Single(context.FailedJobs);
    }
}
