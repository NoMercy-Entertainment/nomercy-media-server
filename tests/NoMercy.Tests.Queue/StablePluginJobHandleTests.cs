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
using Microsoft.Data.Sqlite;
using NoMercy.Queue.MediaServer;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue.Core;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Sqlite;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// The handle a plugin gets at dispatch is the hash of the first payload. It must
/// keep finding the job after the job rewrites its payload, both while it is
/// queued and after it fails.
/// </summary>
[Trait("Category", "Unit")]
public class StablePluginJobHandleTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"queue_handle_{Guid.NewGuid()}.db"
    );

    private IQueueContext? _ctx;

    public void Dispose()
    {
        _ctx?.Dispose();
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private IQueueContext Make(string kind)
    {
        _ctx =
            kind == "sqlite"
                ? SqliteQueueContextFactory.Create(_dbPath)
                : new EfQueueContextAdapter(TestQueueContextFactory.CreateInMemoryContext());
        return _ctx;
    }

    private static QueueJobModel Dispatch(IQueueContext ctx, out string handle)
    {
        const string payload = "{\"phase\":0}";
        handle = QueuePayloadHash.For(payload);
        QueueJobModel job = new()
        {
            Queue = "encoder",
            Payload = payload,
            AvailableAt = DateTime.UtcNow,
        };
        ctx.AddJob(job);
        return job;
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("ef")]
    public void HandleStillFindsTheJobAfterThePayloadIsRewritten(string kind)
    {
        IQueueContext ctx = Make(kind);
        QueueJobModel job = Dispatch(ctx, out string handle);

        ctx.UpdateJobPayload(job.Id, "{\"phase\":1}", DateTime.UtcNow);

        QueueJobModel? found = ctx.FindJobByPayloadHash(handle);
        found.Should().NotBeNull();
        found!.Id.Should().Be(job.Id);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("ef")]
    public void HandleFindsTheFailedJobAfterThePayloadWasRewritten(string kind)
    {
        IQueueContext ctx = Make(kind);
        QueueJobModel job = Dispatch(ctx, out string handle);
        ctx.UpdateJobPayload(job.Id, "{\"phase\":1}", DateTime.UtcNow);
        QueueJobModel current = ctx.FindJob(job.Id)!;

        FailedJobModel failed = new()
        {
            Queue = current.Queue,
            Payload = current.Payload,
            OriginalPayloadHash = current.OriginalPayloadHash,
            Exception = "boom",
        };
        ctx.AddFailedJobAndRemoveJob(failed, current);

        ctx.FindJobByPayloadHash(handle).Should().BeNull();
        ctx.FindFailedJobByPayloadHash(handle).Should().NotBeNull();
    }
}
