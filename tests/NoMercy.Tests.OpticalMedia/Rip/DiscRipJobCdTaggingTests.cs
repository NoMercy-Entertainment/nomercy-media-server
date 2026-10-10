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

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.Encoder.Audio;
using NoMercy.Events;
using NoMercy.Events.DriveMonitor;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Storage;
using NoMercy.Tests.Common.Providers;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// Testable subclass that short-circuits the MediaContext DB fetch, mirroring
/// the pattern in DiscRipJobEncodeDispatchTests (file-scoped there).
/// </summary>
file sealed class TestableDiscRipJob(Folder folder, Library library) : DiscRipJob
{
    protected override Task<(Folder? Folder, Library? Library)> FetchTargetsAsync(
        Ulid folderId,
        Ulid libraryId,
        CancellationToken cancellationToken
    ) => Task.FromResult<(Folder?, Library?)>((folder, library));
}

/// <summary>
/// REQUIREMENT: for a CD rip that both moves its output into the library
/// (<see cref="RipMode.RipAndEncode"/> with a resolved target folder/library)
/// AND carries an identified MusicBrainz release
/// (<see cref="RipRequest.MetadataId"/> set), <see cref="DiscRipJob"/> must
/// re-fetch the full release via <see cref="MusicBrainzReleaseClient"/>,
/// match each ripped track to its MusicBrainz recording by disc position, and
/// tag every successfully-ripped FLAC via <see cref="IAudioMetadataWriter"/>
/// before the files are copied into the library folder — using the release's
/// Cover Art Archive front flag to populate cover art, and never letting a
/// release-not-found or per-track tag-write failure crash the job.
/// </summary>
[Collection("HttpClientProvider")]
[Trait("Category", "Unit")]
public sealed class DiscRipJobCdTaggingTests : ProviderHttpHarness
{
    private static readonly Ulid KnownFolderId = Ulid.NewUlid();
    private static readonly Ulid KnownLibraryId = Ulid.NewUlid();

    public DiscRipJobCdTaggingTests()
        : base(NoMercy.Providers.Helpers.HttpClientNames.MusicBrainz) { }

    private static RipRequest MakeCdRequest(string metadataId, int[] titleIndices) =>
        new(
            DrivePath: "/dev/sr0",
            SelectedTitleIndices: titleIndices,
            MetadataId: metadataId,
            Custom: null,
            LibraryId: KnownLibraryId,
            FolderId: KnownFolderId,
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipAndEncode,
            DiscType: OpticalDiscType.Cd
        );

    private static Folder MakeFolder() =>
        new()
        {
            Id = KnownFolderId,
            Path = "/media/music",
            EncodingPresetFolders = [],
        };

    private static Library MakeLibrary() =>
        new()
        {
            Id = KnownLibraryId,
            Title = "Music",
            Type = "music",
            FolderLibraries = [],
        };

    private static async Task<DiscRipResult[]> MakeSuccessResultsWithRealFilesAsync(
        int[] trackIndices
    )
    {
        List<DiscRipResult> results = [];
        foreach (int i in trackIndices)
        {
            string path = Path.Combine(Path.GetTempPath(), $"cdtag_{Guid.NewGuid():N}_{i:D2}.flac");
            await File.WriteAllBytesAsync(path, []);
            results.Add(
                new(
                    TitleIndex: i,
                    OutputPath: path,
                    Success: true,
                    Duration: TimeSpan.FromMinutes(3),
                    OutputSizeBytes: 20_000_000,
                    Error: null
                )
            );
        }
        return [.. results];
    }

    private static Mock<IStorage> MakeStorageMock(string hostPath)
    {
        Mock<IStorage> storage = new();
        storage
            .Setup(s => s.GetFullPath(It.IsAny<string>()))
            .Returns<string>(rel => hostPath + "/" + rel.TrimStart('/'));
        storage
            .Setup(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        storage
            .Setup(s =>
                s.OpenWriteAsync(
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Stream.Null);
        return storage;
    }

    private static DiscRipJob BuildJob(
        RipRequest request,
        IDiscRipper ripper,
        IAudioMetadataWriter tagWriter,
        IStorage folderStorage
    )
    {
        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(folderStorage);

        TestableDiscRipJob job = new(MakeFolder(), MakeLibrary())
        {
            Request = request,
            OutputDir = Path.GetTempPath(),
            TargetFolderId = KnownFolderId,
            TargetLibraryId = KnownLibraryId,
            TargetLibraryType = "music",
            DiscRipper = ripper,
            IdentificationService = new([], NullLogger<DiscIdentificationService>.Instance),
            StorageFactory = factoryMock.Object,
            StorageDriver = Mock.Of<IStorageDriver>(),
            DriveLockRegistry = new(),
            LoggerFactory = NullLoggerFactory.Instance,
            AudioMetadataWriter = tagWriter,
            MusicBrainzReleaseClient = new(),
        };

        return job;
    }

    private static string ReleaseJson(Guid releaseId, bool hasFrontCover) =>
        $$"""
            {
              "id": "{{releaseId}}",
              "title": "Test Album",
              "artist-credit": [ { "name": "Test Artist", "joinphrase": "" } ],
              "date": "2015-06-01",
              "genres": [ { "name": "Rock" } ],
              "media": [
                {
                  "track-count": 2,
                  "tracks": [
                    {
                      "position": 1,
                      "id": "{{Guid.NewGuid()}}",
                      "title": "First Track",
                      "artist-credit": [ { "name": "Track Artist", "joinphrase": "" } ],
                      "recording": { "id": "{{Guid.NewGuid()}}", "title": "First Track" }
                    },
                    {
                      "position": 2,
                      "id": "{{Guid.NewGuid()}}",
                      "title": "Second Track",
                      "artist-credit": [ { "name": "Track Artist", "joinphrase": "" } ],
                      "recording": { "id": "{{Guid.NewGuid()}}", "title": "Second Track" }
                    }
                  ]
                }
              ],
              "cover-art-archive": { "front": {{(hasFrontCover ? "true" : "false")}} }
            }
            """;

    [Fact]
    public async Task Handle_CdWithMetadataId_TagsEachRippedTrackWithMatchedRecording()
    {
        Guid releaseId = Guid.NewGuid();
        Handler.WhenGet(
            $"release/{releaseId}",
            MockResponse.Json(HttpStatusCode.OK, ReleaseJson(releaseId, true))
        );

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1, 2]);
        try
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
                .ReturnsAsync(ripResults);

            List<AudioMetadata> tagged = [];
            Mock<IAudioMetadataWriter> tagWriterMock = new();
            tagWriterMock
                .Setup(w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<string, AudioMetadata, CancellationToken>(
                    (_, meta, _) => tagged.Add(meta)
                )
                .Returns(Task.CompletedTask);

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(releaseId.ToString(), [1, 2]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            await job.Handle();

            tagged.Should().HaveCount(2);
            tagged[0].Title.Should().Be("First Track");
            tagged[0].Artist.Should().Be("Track Artist");
            tagged[0].AlbumArtist.Should().Be("Test Artist");
            tagged[0].Album.Should().Be("Test Album");
            tagged[0].Year.Should().Be(2015);
            tagged[0].Genre.Should().Be("Rock");
            tagged[0].CoverArt.Should().NotBeNull();
            tagged[1].Title.Should().Be("Second Track");
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_NoFrontCover_CoverArtIsNull()
    {
        Guid releaseId = Guid.NewGuid();
        Handler.WhenGet(
            $"release/{releaseId}",
            MockResponse.Json(HttpStatusCode.OK, ReleaseJson(releaseId, false))
        );

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1]);
        try
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
                .ReturnsAsync(ripResults);

            List<AudioMetadata> tagged = [];
            Mock<IAudioMetadataWriter> tagWriterMock = new();
            tagWriterMock
                .Setup(w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<string, AudioMetadata, CancellationToken>(
                    (_, meta, _) => tagged.Add(meta)
                )
                .Returns(Task.CompletedTask);

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(releaseId.ToString(), [1]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            await job.Handle();

            tagged.Should().ContainSingle();
            tagged[0].CoverArt.Should().BeNull();
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_ReleaseNotFound_SkipsTaggingWithoutThrowing()
    {
        string metadataId = Guid.NewGuid().ToString();
        Handler.WhenGet($"release/{metadataId}", MockResponse.Status(HttpStatusCode.NotFound));

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1]);
        try
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
                .ReturnsAsync(ripResults);

            Mock<IAudioMetadataWriter> tagWriterMock = new();

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(metadataId, [1]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            Exception? ex = await Record.ExceptionAsync(() => job.Handle());

            ex.Should()
                .BeNull("a not-found release must degrade to untagged FLACs, not crash the job");
            tagWriterMock.Verify(
                w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Never
            );
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_ReleaseFetchThrows_SkipsTaggingWithoutCrashingJob()
    {
        // 500 (not a soft-fail status, not Queue-retried) makes
        // WithAllAppends genuinely throw — unlike the 404 case above, this
        // exercises TagCdTracksAsync's own try/catch around the MusicBrainz
        // release fetch rather than the "release is null" branch below it.
        string metadataId = Guid.NewGuid().ToString();
        Handler.WhenGet(
            $"release/{metadataId}",
            MockResponse.Status(HttpStatusCode.InternalServerError)
        );

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1]);
        try
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
                .ReturnsAsync(ripResults);

            Mock<IAudioMetadataWriter> tagWriterMock = new();

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(metadataId, [1]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            Exception? ex = await Record.ExceptionAsync(() => job.Handle());

            ex.Should()
                .BeNull(
                    "a release-fetch failure must degrade to untagged FLACs, not crash the job"
                );
            tagWriterMock.Verify(
                w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Never
            );
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_TrackNotMatchedInMedium_FallsBackToTrackNumberTitle()
    {
        Guid releaseId = Guid.NewGuid();
        // Only 2 tracks in the release medium, but the disc rip has 3 —
        // the position lookup in TagCdTracksAsync should fail to find track
        // 3, falling back to "Track 03" / the album artist credit.
        Handler.WhenGet(
            $"release/{releaseId}",
            MockResponse.Json(HttpStatusCode.OK, ReleaseJson(releaseId, false))
        );

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1, 2, 3]);
        try
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
                .ReturnsAsync(ripResults);

            List<AudioMetadata> tagged = [];
            Mock<IAudioMetadataWriter> tagWriterMock = new();
            tagWriterMock
                .Setup(w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<string, AudioMetadata, CancellationToken>(
                    (_, meta, _) => tagged.Add(meta)
                )
                .Returns(Task.CompletedTask);

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(releaseId.ToString(), [1, 2, 3]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            await job.Handle();

            tagged.Should().HaveCount(3);
            tagged[2].Title.Should().Be("Track 03");
            tagged[2].Artist.Should().Be("Test Artist", "falls back to the album artist credit");
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_TagWriterThrowsForOneTrack_ContinuesWithRemainingTracks()
    {
        Guid releaseId = Guid.NewGuid();
        Handler.WhenGet(
            $"release/{releaseId}",
            MockResponse.Json(HttpStatusCode.OK, ReleaseJson(releaseId, false))
        );

        DiscRipResult[] ripResults = await MakeSuccessResultsWithRealFilesAsync([1, 2]);
        try
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
                .ReturnsAsync(ripResults);

            Mock<IAudioMetadataWriter> tagWriterMock = new();
            tagWriterMock
                .SetupSequence(w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .ThrowsAsync(new InvalidOperationException("disk full"))
                .Returns(Task.CompletedTask);

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(releaseId.ToString(), [1, 2]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            Exception? ex = await Record.ExceptionAsync(() => job.Handle());

            ex.Should().BeNull("one track's tag failure must not stop the rest from being tagged");
            tagWriterMock.Verify(
                w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Exactly(2)
            );
        }
        finally
        {
            foreach (DiscRipResult r in ripResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }

    [Fact]
    public async Task Handle_CdWithMetadataId_OnlyTagsSuccessfulRips()
    {
        Guid releaseId = Guid.NewGuid();
        Handler.WhenGet(
            $"release/{releaseId}",
            MockResponse.Json(HttpStatusCode.OK, ReleaseJson(releaseId, false))
        );

        DiscRipResult[] successFile = await MakeSuccessResultsWithRealFilesAsync([1]);
        DiscRipResult[] mixedResults =
        [
            successFile[0],
            new(
                TitleIndex: 2,
                OutputPath: Path.Combine(
                    Path.GetTempPath(),
                    $"cdtag_missing_{Guid.NewGuid():N}.flac"
                ),
                Success: false,
                Duration: TimeSpan.Zero,
                OutputSizeBytes: 0,
                Error: "read error"
            ),
        ];
        try
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
                .ReturnsAsync(mixedResults);

            Mock<IAudioMetadataWriter> tagWriterMock = new();

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            DiscRipJob job = BuildJob(
                MakeCdRequest(releaseId.ToString(), [1, 2]),
                ripperMock.Object,
                tagWriterMock.Object,
                MakeStorageMock("/media/music").Object
            );

            await job.Handle();

            tagWriterMock.Verify(
                w =>
                    w.WriteTagsAsync(
                        It.Is<string>(p => p.Contains("cdtag_missing")),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Never,
                "the failed track must never be tagged"
            );
            tagWriterMock.Verify(
                w =>
                    w.WriteTagsAsync(
                        It.IsAny<string>(),
                        It.IsAny<AudioMetadata>(),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Once
            );
        }
        finally
        {
            foreach (DiscRipResult r in mixedResults)
                if (File.Exists(r.OutputPath))
                    File.Delete(r.OutputPath);
        }
    }
}
