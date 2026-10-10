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

using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Collection("EventBusProvider")]
public class ImportFinishedEventTests
{
    private sealed class ProbeImportJob(Func<Task<int?>> import) : AbstractMediaJob
    {
        public override string QueueName => "import";
        public override int Priority => 5;

        public override Task Handle() => HandleWithFinishEventAsync(import);
    }

    private static List<MediaImportFinishedEvent> Capture()
    {
        InMemoryEventBus bus = new();
        List<MediaImportFinishedEvent> finished = [];
        bus.Subscribe<MediaImportFinishedEvent>(
            (e, _) =>
            {
                finished.Add(e);
                return Task.CompletedTask;
            }
        );
        EventBusProvider.Configure(bus);
        return finished;
    }

    [Fact]
    public async Task ASuccessfulImport_PublishesTheTitlesItAdded()
    {
        List<MediaImportFinishedEvent> finished = Capture();
        Ulid libraryId = Ulid.NewUlid();
        ProbeImportJob job = new(() => Task.FromResult<int?>(1)) { LibraryId = libraryId };

        await job.Handle();

        MediaImportFinishedEvent only = Assert.Single(finished);
        Assert.Equal(libraryId, only.LibraryId);
        Assert.Equal(1, only.Added);
        Assert.Equal(0, only.Failed);
    }

    [Fact]
    public async Task AnImportThatEndsWithoutAResult_PublishesAFailure()
    {
        List<MediaImportFinishedEvent> finished = Capture();
        ProbeImportJob job = new(() => Task.FromResult<int?>(null));

        await job.Handle();

        MediaImportFinishedEvent only = Assert.Single(finished);
        Assert.Equal(0, only.Added);
        Assert.Equal(1, only.Failed);
    }

    [Fact]
    public async Task AFailureThatWillBeRetried_PublishesNothing()
    {
        List<MediaImportFinishedEvent> finished = Capture();
        ProbeImportJob job = new(() => throw new InvalidOperationException("boom"));
        job.ReceiveAttempt(1, 3);

        await Assert.ThrowsAsync<InvalidOperationException>(job.Handle);

        Assert.Empty(finished);
    }

    [Fact]
    public async Task AFailureOnTheLastAttempt_PublishesOneFailure()
    {
        List<MediaImportFinishedEvent> finished = Capture();
        ProbeImportJob job = new(() => throw new InvalidOperationException("boom"));
        job.ReceiveAttempt(3, 3);

        await Assert.ThrowsAsync<InvalidOperationException>(job.Handle);

        MediaImportFinishedEvent only = Assert.Single(finished);
        Assert.Equal(1, only.Failed);
    }

    [Fact]
    public async Task ATransientRetryAfterTheLastAttemptFailed_StillCountsOnce()
    {
        // QueueWorker retries a transient SQLite error inside the same
        // reservation, so Handle can run again after it reported a failure.
        List<MediaImportFinishedEvent> finished = Capture();
        int runs = 0;
        ProbeImportJob job = new(() =>
            ++runs == 1
                ? throw new InvalidOperationException("database is locked")
                : Task.FromResult<int?>(1)
        );
        job.ReceiveAttempt(3, 3);

        await Assert.ThrowsAsync<InvalidOperationException>(job.Handle);
        await job.Handle();

        Assert.Single(finished);
    }

    [Theory]
    [InlineData(typeof(MovieImportJob))]
    [InlineData(typeof(ShowImportJob))]
    [InlineData(typeof(ReleaseImportJob))]
    public void TheQueuedPayload_CarriesNoAttemptOrScanState(Type jobType)
    {
        object job = Activator.CreateInstance(jobType)!;

        string payload = SerializationHelper.Serialize(
            (NoMercyQueue.Core.Interfaces.IShouldQueue)job
        );

        Assert.DoesNotContain("attempt", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scanId", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("addedToLibrary", payload, StringComparison.OrdinalIgnoreCase);
    }
}
