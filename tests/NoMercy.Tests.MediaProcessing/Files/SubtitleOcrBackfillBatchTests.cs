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
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs.SubtitleJobs;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Files;

// A title with many orphaned bitmap subtitles used to fill the queue with one
// OCR job per file; it is one job per title folder now.
[Trait("Category", "Unit")]
public sealed class SubtitleOcrBackfillBatchTests
{
    private static readonly OrphanedBitmapSubtitle[] Orphans =
    [
        new("Movie.(2008).NoMercy.chi.full.mks", "Movie.(2008).NoMercy", "chi", "full"),
        new("Movie.(2008).NoMercy.cze.full.mks", "Movie.(2008).NoMercy", "cze", "full"),
        new("Movie.(2008).NoMercy.eng.alt.mks", "Movie.(2008).NoMercy", "eng", "alt"),
    ];

    [Fact]
    public void BuildOcrBackfillJob_ManyOrphans_IsOneJobCarryingEveryTrack()
    {
        Folder folder = new() { Id = Ulid.NewUlid(), DriverId = Ulid.NewUlid() };

        SubtitleOcrBackfillJob? job = FileManager.BuildOcrBackfillJob(
            folder,
            "/Films/Movie.(2008)",
            Orphans
        );

        job.Should().NotBeNull();
        job!.HostFolder.Should().Be("/Films/Movie.(2008)");
        job.Tracks.Should().Equal(Orphans);
    }

    [Fact]
    public void BuildOcrBackfillJob_NoOrphans_IsNoJob()
    {
        Folder folder = new() { Id = Ulid.NewUlid(), DriverId = Ulid.NewUlid() };

        FileManager.BuildOcrBackfillJob(folder, "/Films/Movie.(2008)", []).Should().BeNull();
    }

    [Fact]
    public void ABatchPayload_SurvivesTheQueueRoundTrip()
    {
        SubtitleOcrBackfillJob job = new(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "/Films/Movie.(2008)",
            Orphans
        );

        SubtitleOcrBackfillJob restored = SerializationHelper.Deserialize<SubtitleOcrBackfillJob>(
            SerializationHelper.Serialize(job)
        );

        restored.PendingTracks().Should().Equal(Orphans);
    }

    // Rows queued before the batch change carry one file in the flat fields.
    [Fact]
    public void ASingleFilePayloadFromBeforeTheBatch_StillRunsItsOneTrack()
    {
        SubtitleOcrBackfillJob legacy = new()
        {
            FolderId = Ulid.NewUlid().ToString(),
            DriverId = Ulid.NewUlid().ToString(),
            HostFolder = "/Films/Movie.(2008)",
            SupFileName = "Movie.(2008).NoMercy.dan.full.mks",
            MediaTitle = "Movie.(2008).NoMercy",
            Language = "dan",
            Variant = "full",
        };

        SubtitleOcrBackfillJob restored = SerializationHelper.Deserialize<SubtitleOcrBackfillJob>(
            SerializationHelper.Serialize(legacy)
        );

        restored
            .PendingTracks()
            .Should()
            .Equal(
                new OrphanedBitmapSubtitle(
                    "Movie.(2008).NoMercy.dan.full.mks",
                    "Movie.(2008).NoMercy",
                    "dan",
                    "full"
                )
            );
    }
}
