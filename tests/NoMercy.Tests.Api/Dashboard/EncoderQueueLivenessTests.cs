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

using NoMercy.Api.Controllers.V1.Dashboard.Admin;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.SubtitleJobs;
using NoMercyQueue;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// "Encoding now" and "waiting" is the split the whole dashboard hangs off — one
/// screen on mobile each, two sections on the web panel. A coordinator stamps
/// itself the moment it has decomposed, so with one runner every episode of a
/// season claims to be in flight simultaneously. Only a held reservation on
/// the row itself means a runner is actually on it — one job runs every
/// bundle inline on its own row now, so there is no separate child reservation
/// to check.
/// </summary>
[Trait("Category", "Unit")]
public class EncoderQueueLivenessTests
{
    [Fact]
    public void ReservedCoordinatorRow_IsInFlight()
    {
        TasksController.IsEncodeInFlight(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void DecomposedButWaitingForARunner_IsStillQueued()
    {
        TasksController.IsEncodeInFlight(null).Should().BeFalse();
    }

    [Fact]
    public void AnEncodePayload_IsReadAsAnEncode()
    {
        VideoEncodeJob encode = new()
        {
            Id = "4114390",
            FolderId = Ulid.NewUlid(),
            LibraryId = Ulid.NewUlid(),
            InputFile = "/Anime/Blue Lock/S01E19.mkv",
        };

        VideoEncodeJob? read = TasksController.ReadEncodeJob(SerializationHelper.Serialize(encode));

        read.Should().NotBeNull();
        read!.Id.ToString().Should().Be("4114390");
        read.InputFile.Should().Be("/Anime/Blue Lock/S01E19.mkv");
    }

    /// <summary>
    /// The encoder queue is shared with maintenance work, on purpose — a preview
    /// rebuild is ffmpeg and must not run alongside an encode. Those jobs carry a
    /// FolderId of their own, so deserializing them straight into a
    /// <see cref="VideoEncodeJob"/> produced something that looked like an encode
    /// with a real profile and no title, id, artwork or file: a card for a title
    /// that does not exist.
    /// </summary>
    [Fact]
    public void APreviewRebuildPayload_IsNotAnEncode()
    {
        SpriteSheetUpgradeJob maintenance = new(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "/Anime/Frieren/S00E01",
            "Frieren S00E01"
        );

        TasksController
            .ReadEncodeJob(SerializationHelper.Serialize(maintenance))
            .Should()
            .BeNull("a preview rebuild is not an encode and must not reach the encode panel");
    }

    [Fact]
    public void AnOcrBackfillPayload_IsNotAnEncode()
    {
        SubtitleOcrBackfillJob maintenance = new(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "/Anime/Frieren/S01E01",
            "eng.full.sup",
            "Frieren S01E01",
            "eng",
            "full"
        );

        TasksController
            .ReadEncodeJob(SerializationHelper.Serialize(maintenance))
            .Should()
            .BeNull("the OCR backfill shares the encoder queue and is not an encode either");
    }

    [Fact]
    public void AnUnreadablePayload_IsNotAnEncode()
    {
        TasksController.ReadEncodeJob("{ this is not json").Should().BeNull();
    }

    /// <summary>
    /// Not being an encode is not the same as not being work. Hours of preview
    /// rebuilds can sit on this queue, and a panel that shows none of them tells
    /// the operator the server is idle while it is busy — which is the complaint
    /// that started all of this.
    /// </summary>
    [Fact]
    public void APreviewRebuild_IsReportedAsMaintenance()
    {
        SpriteSheetUpgradeJob job = new(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "Libraries/Anime/BLUE.LOCK.(2022)/BLUE.LOCK.S01E01",
            "BLUE.LOCK.S01E01"
        );

        MaintenanceJob? entry = TasksController.ReadMaintenanceJob(
            new()
            {
                Id = 41,
                Priority = 1,
                Queue = "encoder",
                Payload = SerializationHelper.Serialize(job),
            }
        );

        entry.Should().NotBeNull();
        entry!.Dto.Kind.Should().Be("preview");
        entry.Dto.Title.Should().Be("BLUE.LOCK.S01E01");
        entry.Dto.Status.Should().Be("pending", "no runner holds this row");
        entry
            .Dto.PayloadId.Should()
            .Be(
                "Libraries/Anime/BLUE.LOCK.(2022)/BLUE.LOCK.S01E01",
                "the folder is what this job is about and it does not move, so it is the list key"
            );
        entry
            .HostFolder.Should()
            .Be(
                "Libraries/Anime/BLUE.LOCK.(2022)/BLUE.LOCK.S01E01",
                "the folder is the key the title and the still are resolved by"
            );
    }

    /// <summary>
    /// The job payload knows a folder name and nothing else. Left at that, the
    /// card is a grey frame over a filename for work that can hold the server for
    /// hours — so the folder is resolved back to the file it belongs to and the
    /// card gets the same title and still an encode of that episode would show.
    /// </summary>
    [Fact]
    public void AMaintenanceCard_TakesTheTitleAndStillOfTheEpisode()
    {
        QueueJobDto dto = new() { Title = "BLUE.LOCK.S01E01", Kind = "preview" };

        TasksController.ApplyMediaDetails(
            dto,
            new()
            {
                HostFolder = "Libraries/Anime/BLUE.LOCK.(2022)/BLUE.LOCK.S01E01",
                Episode = new()
                {
                    Title = "Dream",
                    SeasonNumber = 1,
                    EpisodeNumber = 1,
                    Still = "/still.jpg",
                    Tv = new() { Title = "BLUE LOCK" },
                },
            }
        );

        dto.Title.Should().Be("BLUE LOCK S01E01 Dream NoMercy");
        dto.Backdrop.Should()
            .Be("/still.jpg", "an empty frame is what the card looked like before");
    }

    [Fact]
    public void AMaintenanceCardForAFilm_TakesTheMovieBackdrop()
    {
        QueueJobDto dto = new() { Title = "The.Punisher.One.Last.Kill", Kind = "preview" };

        TasksController.ApplyMediaDetails(
            dto,
            new()
            {
                HostFolder = "Libraries/Marvels/Films/The.Punisher.One.Last.Kill.(2026)",
                Movie = new()
                {
                    Title = "The Punisher: One Last Kill",
                    ReleaseDate = new(2026, 1, 1),
                    Backdrop = "/backdrop.jpg",
                },
            }
        );

        dto.Title.Should().Be("The Punisher: One Last Kill (2026) NoMercy");
        dto.Backdrop.Should().Be("/backdrop.jpg");
    }

    /// <summary>
    /// A file the library has no metadata for keeps the folder name the job
    /// carried. Naming the folder beats naming nothing.
    /// </summary>
    [Fact]
    public void AMaintenanceCardWithNoMetadataBehindIt_KeepsTheFolderName()
    {
        QueueJobDto dto = new() { Title = "BLUE.LOCK.S01E01", Kind = "preview" };

        TasksController.ApplyMediaDetails(dto, new() { HostFolder = "Libraries/Anime/Orphan" });

        dto.Title.Should().Be("BLUE.LOCK.S01E01");
        dto.Backdrop.Should().BeNull();
    }

    [Fact]
    public void AReservedMaintenanceRow_IsRunning()
    {
        SpriteSheetUpgradeJob job = new(
            Ulid.NewUlid().ToString(),
            Ulid.NewUlid().ToString(),
            "Libraries/Anime/Frieren/S01E01",
            "Frieren S01E01"
        );

        MaintenanceJob? entry = TasksController.ReadMaintenanceJob(
            new()
            {
                Id = 42,
                Priority = 1,
                Queue = "encoder",
                Payload = SerializationHelper.Serialize(job),
                ReservedAt = DateTime.UtcNow,
            }
        );

        entry!.Dto.Status.Should().Be("running");
    }

    [Fact]
    public void AnEncodeIsNotMaintenance()
    {
        VideoEncodeJob encode = new() { Id = "4114390", FolderId = Ulid.NewUlid() };

        TasksController
            .ReadMaintenanceJob(
                new()
                {
                    Id = 43,
                    Priority = 4,
                    Queue = "encoder",
                    Payload = SerializationHelper.Serialize(encode),
                }
            )
            .Should()
            .BeNull("an encode goes through the encode path, or it would appear twice");
    }
}
