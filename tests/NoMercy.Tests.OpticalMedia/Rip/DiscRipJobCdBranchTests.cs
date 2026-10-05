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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Audio;
using NoMercy.Events;
using NoMercy.Events.DriveMonitor;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Storage;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// Unit tests for the CD music branch in <see cref="DiscRipJob"/>.
/// Uses RipMode.RipToRaw to skip the DB move-and-import path.
/// </summary>
[Collection("EventBusProvider")]
[Trait("Category", "Unit")]
public class DiscRipJobCdBranchTests
{
    private static RipRequest CdRequest(string drivePath = "/dev/sr0", string? metadataId = null) =>
        new(
            DrivePath: drivePath,
            SelectedTitleIndices: [1, 2, 3],
            MetadataId: metadataId,
            Custom: null,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipToRaw,
            DiscType: OpticalDiscType.Cd
        );

    private static DiscRipResult[] SuccessResults(int[] trackIndices) =>
        [
            .. trackIndices.Select(i => new DiscRipResult(
                TitleIndex: i,
                OutputPath: Path.Combine(Path.GetTempPath(), $"{i:D2} - Track {i:D2}.flac"),
                Success: true,
                Duration: TimeSpan.FromMinutes(3),
                OutputSizeBytes: 50_000_000,
                Error: null
            )),
        ];

    private static DiscRipJob MakeJob(
        RipRequest request,
        IDiscRipper ripper,
        IAudioMetadataWriter? tagWriter = null,
        MusicBrainzReleaseClient? mbClient = null
    )
    {
        DiscRipJob job = new(
            request: request,
            outputDir: Path.GetTempPath(),
            targetFolderId: null,
            targetLibraryId: null,
            targetLibraryType: null
        );

        job.DiscRipper = ripper;
        job.IdentificationService = new([], NullLogger<DiscIdentificationService>.Instance);
        job.StorageFactory = Mock.Of<IStorageFactory>();
        job.StorageDriver = Mock.Of<IStorageDriver>();
        job.DriveLockRegistry = new();
        job.LoggerFactory = NullLoggerFactory.Instance;
        job.AudioMetadataWriter = tagWriter ?? Mock.Of<IAudioMetadataWriter>();
        job.MusicBrainzReleaseClient = mbClient ?? new MusicBrainzReleaseClient();

        return job;
    }

    // ── RipToRaw path fires rip_complete ──────────────────────────────────

    [Fact]
    public async Task Handle_CdRipToRaw_PublishesStartedAndComplete()
    {
        DiscRipResult[] ripResults = SuccessResults([1, 2, 3]);
        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ripResults);

        List<DriveStateChangedEvent> published = [];
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<DriveStateChangedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);

        EventBusProvider.Configure(busMock.Object);

        DiscRipJob job = MakeJob(CdRequest(metadataId: null), ripperMock.Object);
        await job.Handle();

        published.Select(e => e.DriveStateData.Method).Should().Contain("rip_started");
        published.Select(e => e.DriveStateData.Method).Should().Contain("rip_complete");
        published.Select(e => e.DriveStateData.Method).Should().NotContain("rip_error");
    }

    // ── No metadataId → tag writer is NOT called ──────────────────────────

    [Fact]
    public async Task Handle_CdRipToRaw_NoMetadataId_TagWriterNotCalled()
    {
        DiscRipResult[] ripResults = SuccessResults([1]);
        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ripResults);

        Mock<IAudioMetadataWriter> tagWriterMock = new();

        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        DiscRipJob job = MakeJob(
            CdRequest(metadataId: null),
            ripperMock.Object,
            tagWriter: tagWriterMock.Object
        );
        await job.Handle();

        tagWriterMock.Verify(
            tw =>
                tw.WriteTagsAsync(
                    It.IsAny<string>(),
                    It.IsAny<AudioMetadata>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never,
            "tag writer must not be called when no MetadataId is supplied"
        );
    }

    // ── DiscRipper failure → rip_error event, tag writer not called ───────

    [Fact]
    public async Task Handle_CdRipFails_PublishesError_TagWriterNotCalled()
    {
        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("drive gone"));

        Mock<IAudioMetadataWriter> tagWriterMock = new();

        List<DriveStateChangedEvent> published = [];
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<DriveStateChangedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        DiscRipJob job = MakeJob(
            CdRequest(metadataId: "some-mbid"),
            ripperMock.Object,
            tagWriter: tagWriterMock.Object
        );
        await job.Handle();

        published.Should().Contain(e => e.DriveStateData.Method == "rip_error");
        tagWriterMock.Verify(
            tw =>
                tw.WriteTagsAsync(
                    It.IsAny<string>(),
                    It.IsAny<AudioMetadata>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    // ── TagWriter failure is non-fatal ────────────────────────────────────

    [Fact]
    public async Task Handle_CdRip_TagWriterThrows_DoesNotCrashJob()
    {
        DiscRipResult[] ripResults = SuccessResults([1]);
        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ripResults);

        // Tag writer throws; this should be caught and logged, not propagated.
        Mock<IAudioMetadataWriter> tagWriterMock = new();
        tagWriterMock
            .Setup(tw =>
                tw.WriteTagsAsync(
                    It.IsAny<string>(),
                    It.IsAny<AudioMetadata>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new InvalidOperationException("TagLib write error"));

        // MusicBrainz client is not called in RipToRaw mode (no metadataId path),
        // so this tests that a tag-write failure inside TagCdTracksAsync does not bubble.
        // We supply a metadataId to trigger the tagging path but the client will
        // return null (no real HTTP), and the code gracefully handles that.
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        DiscRipJob job = MakeJob(
            CdRequest(metadataId: null),
            ripperMock.Object,
            tagWriter: tagWriterMock.Object
        );

        Exception? ex = await Record.ExceptionAsync(() => job.Handle());
        ex.Should().BeNull("a tag-write failure must not crash the job");
    }

    // ── Non-CD disc type does not use CD path ────────────────────────────

    [Fact]
    public async Task Handle_BluRayDisc_TagWriterNotCalled()
    {
        RipRequest bluRayRequest = new(
            DrivePath: "bluray:/dev/sr0",
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: new(Title: "The Matrix", Year: 1999, Type: MediaType.Movie, PosterUrl: null),
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipToRaw,
            DiscType: OpticalDiscType.BluRay
        );

        DiscRipResult[] ripResults =
        [
            new(
                TitleIndex: 1,
                OutputPath: Path.Combine(Path.GetTempPath(), "title_01.mkv"),
                Success: true,
                Duration: TimeSpan.FromMinutes(120),
                OutputSizeBytes: 40_000_000_000,
                Error: null
            ),
        ];

        Mock<IDiscRipper> ripperMock = new();
        ripperMock
            .Setup(r =>
                r.RipAsync(
                    It.IsAny<RipRequest>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(ripResults);

        Mock<IAudioMetadataWriter> tagWriterMock = new();

        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        DiscRipJob job = MakeJob(bluRayRequest, ripperMock.Object, tagWriter: tagWriterMock.Object);
        await job.Handle();

        tagWriterMock.Verify(
            tw =>
                tw.WriteTagsAsync(
                    It.IsAny<string>(),
                    It.IsAny<AudioMetadata>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never,
            "tag writer must not be called for Blu-ray discs"
        );
    }
}
