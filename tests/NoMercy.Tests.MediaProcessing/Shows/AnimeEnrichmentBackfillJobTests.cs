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
using NoMercy.MediaProcessing.Shows;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Shows;

// Handle() opens its own AppDbContext() against the real configured store
// (mirrors PaletteBackfillJob, which is untested at this level for the same
// reason) - only the parts reachable without touching that context are
// covered here. AnimeEnrichmentBackfillStateTests covers the cursor/complete
// logic itself against an injected in-memory context.
public class AnimeEnrichmentBackfillJobTests
{
    [Fact]
    public void QueueName_IsExtras()
    {
        AnimeEnrichmentBackfillJob job = new(Mock.Of<IAnimeEnrichmentService>());

        job.QueueName.Should().Be("extras");
    }

    // Matches ShowExtrasJob/MovieExtrasJob (priority 1), not the queue's
    // absolute floor: priority 0 sorted this job strictly behind every other
    // extras-queue job forever, including ones enqueued after it - on a live
    // server the backlog never actually empties, so the backfill effectively
    // never got a second turn. Verified live: stuck at the same batch across
    // three server restarts until manually re-prioritized each time.
    [Fact]
    public void Priority_MatchesExtrasQueueSiblingsSoItIsNotStarvedForever()
    {
        AnimeEnrichmentBackfillJob job = new(Mock.Of<IAnimeEnrichmentService>());

        job.Priority.Should().Be(1);
    }

    [Fact]
    public async Task Handle_NoServicesInjected_DoesNotThrow()
    {
        // The queue's parameterless constructor path with no InjectStorageServices
        // call (e.g. a direct Handle() invocation in a test double) must no-op
        // rather than throw a NullReferenceException.
        AnimeEnrichmentBackfillJob job = new();

        Func<Task> act = () => job.Handle();

        await act.Should().NotThrowAsync();
    }

    // JobQueue.Enqueue drops a Dispatch() whose serialized payload matches an
    // already-present row (its own dedup check), and a self-redispatched
    // job's OWN row is still present - reserved, not yet deleted - while
    // Handle() runs (QueueWorker only deletes it in a finally block AFTER
    // Handle() returns). A parameterless AnimeEnrichmentBackfillJob() always
    // serializes identically, so self-redispatch always collided with its
    // own row: the insert silently no-op'd, the worker then deleted the old
    // row, and the backfill died with zero trace - reproduced live, twice.
    // DispatchedAfterTvCursor/MovieCursor exist purely to break that
    // collision: two batches at different cursor positions must serialize to
    // DIFFERENT payloads, or this bug is back.
    [Fact]
    public void SerializedPayload_DiffersAcrossCursorPositions_SoSelfRedispatchIsNeverDroppedAsADuplicate()
    {
        AnimeEnrichmentBackfillJob first = new() { DispatchedAfterTvCursor = 100 };
        AnimeEnrichmentBackfillJob second = new() { DispatchedAfterTvCursor = 125 };

        string firstPayload = SerializationHelper.Serialize(first);
        string secondPayload = SerializationHelper.Serialize(second);

        firstPayload.Should().NotBe(secondPayload);
        firstPayload.Should().Contain("100");
        secondPayload.Should().Contain("125");
    }

    // Same collision, the movie-cursor axis - both cursors advance
    // independently (a batch can process only tv, only movies, or both).
    [Fact]
    public void SerializedPayload_DiffersAcrossMovieCursorPositions()
    {
        AnimeEnrichmentBackfillJob first = new() { DispatchedAfterMovieCursor = 7 };
        AnimeEnrichmentBackfillJob second = new() { DispatchedAfterMovieCursor = 42 };

        SerializationHelper.Serialize(first).Should().NotBe(SerializationHelper.Serialize(second));
    }
}
