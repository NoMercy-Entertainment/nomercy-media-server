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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Events;
using NoMercy.Events.DriveMonitor;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;
using NoMercyQueue.Core.Interfaces;
using OpticalMediaType = NoMercy.OpticalMedia.Metadata.MediaType;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// Testable subclass that short-circuits the MediaContext DB fetch by
/// returning pre-seeded objects. This avoids a live SQLite file while
/// still exercising the full dispatch logic.
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
/// Tests for the RipAndEncode video dispatch path added in Wave 3.
/// The static <see cref="QueueRunner"/> is bypassed by injecting
/// <see cref="IJobDispatcher"/> directly via <see cref="DiscRipJob.JobDispatcher"/>.
/// The MediaContext DB fetch is bypassed via <see cref="TestableDiscRipJob"/>.
/// </summary>
[Collection("EventBusProvider")]
[Trait("Category", "Unit")]
public class DiscRipJobEncodeDispatchTests
{
    private static readonly Ulid KnownFolderId = Ulid.NewUlid();
    private static readonly Ulid KnownLibraryId = Ulid.NewUlid();
    private static readonly Ulid KnownPresetId = Ulid.NewUlid();

    private static RipRequest MakeVideoRequest(
        OpticalDiscType discType = OpticalDiscType.Dvd,
        string? encodingProfileId = null,
        RipMode mode = RipMode.RipAndEncode
    ) =>
        new(
            DrivePath: "D:\\",
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: new(
                Title: "Test Film",
                Year: 2024,
                Type: OpticalMediaType.Movie,
                PosterUrl: null
            ),
            LibraryId: KnownLibraryId,
            FolderId: KnownFolderId,
            EncodingProfileId: encodingProfileId,
            AudioTracks: [],
            Subtitles: [],
            Mode: mode,
            DiscType: discType
        );

    private static EncodingPresetFolder MakePresetLink(Ulid presetId) =>
        new()
        {
            PresetId = presetId,
            FolderId = KnownFolderId,
            IsDefault = false,
            Preset = new()
            {
                Id = presetId,
                Name = "Test Preset",
                ProfileJson = "{}",
            },
        };

    private static Folder MakeFolder(IEnumerable<EncodingPresetFolder>? presetLinks = null) =>
        new()
        {
            Id = KnownFolderId,
            Path = "/media/movies",
            EncodingPresetFolders = [.. presetLinks ?? []],
        };

    private static Library MakeLibrary() =>
        new()
        {
            Id = KnownLibraryId,
            Title = "Movies",
            Type = "movie",
            FolderLibraries = [],
        };

    private static DiscRipResult MakeRipResult(string outputPath = "/tmp/title_01.mkv") =>
        new(
            TitleIndex: 1,
            OutputPath: outputPath,
            Success: true,
            Duration: TimeSpan.FromMinutes(90),
            OutputSizeBytes: 1_000_000,
            Error: null
        );

    private static Mock<IStorage> MakeStorageMock(string hostPath)
    {
        Mock<IStorage> storageMock = new();
        storageMock
            .Setup(s => s.GetFullPath(It.IsAny<string>()))
            .Returns<string>(relative => hostPath + "/" + relative.TrimStart('/'));
        storageMock
            .Setup(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        storageMock
            .Setup(s =>
                s.OpenWriteAsync(
                    It.IsAny<string>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(Stream.Null);
        return storageMock;
    }

    private static DiscRipJob BuildJob(
        RipRequest request,
        IDiscRipper ripper,
        IJobDispatcher? jobDispatcher,
        IStorage folderStorage,
        Folder folder,
        Library library
    )
    {
        Mock<IStorageFactory> factoryMock = new();
        factoryMock
            .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
            .Returns(folderStorage);

        TestableDiscRipJob job = new(folder, library)
        {
            Request = request,
            OutputDir = Path.GetTempPath(),
            TargetFolderId = folder.Id,
            TargetLibraryId = library.Id,
            TargetLibraryType = library.Type,
            DiscRipper = ripper,
            IdentificationService = new([], NullLogger<DiscIdentificationService>.Instance),
            StorageFactory = factoryMock.Object,
            StorageDriver = Mock.Of<IStorageDriver>(),
            DriveLockRegistry = new(),
            LoggerFactory = NullLoggerFactory.Instance,
            JobDispatcher = jobDispatcher,
        };

        return job;
    }

    // ── Dispatch with explicit matching preset ────────────────────────────

    [Fact]
    public async Task Handle_VideoRipAndEncode_ExplicitMatchingPreset_DispatchesVideoEncodeJobWithPresetId()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "title_01.mkv");
        await File.WriteAllBytesAsync(tempFile, []);

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
                .ReturnsAsync([MakeRipResult(tempFile)]);

            List<VideoEncodeJob> dispatched = [];
            Mock<IJobDispatcher> dispatcherMock = new();
            dispatcherMock
                .Setup(d =>
                    d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>())
                )
                .Callback<IShouldQueue, string, int>(
                    (job, _, _) =>
                    {
                        if (job is VideoEncodeJob encodeJob)
                            dispatched.Add(encodeJob);
                    }
                );

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            Folder folder = MakeFolder([MakePresetLink(KnownPresetId)]);
            Library library = MakeLibrary();
            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");

            RipRequest request = MakeVideoRequest(
                discType: OpticalDiscType.Dvd,
                encodingProfileId: KnownPresetId.ToString()
            );

            DiscRipJob job = BuildJob(
                request,
                ripperMock.Object,
                dispatcherMock.Object,
                storageMock.Object,
                folder,
                library
            );

            await job.Handle();

            dispatched.Should().HaveCount(1, "one VideoEncodeJob must be dispatched per title");

            VideoEncodeJob encodeJob = dispatched[0];
            encodeJob.LibraryId.Should().Be(KnownLibraryId);
            encodeJob.FolderId.Should().Be(KnownFolderId);
            encodeJob.InputFile.Should().NotBeNullOrEmpty();
            encodeJob.PresetId.Should().Be(KnownPresetId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ── Dispatch without matching preset (falls back to null) ────────────

    [Fact]
    public async Task Handle_VideoRipAndEncode_EncodingProfileIdNotInFolder_DispatchesWithNullPresetId()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "title_02.mkv");
        await File.WriteAllBytesAsync(tempFile, []);

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
                .ReturnsAsync([MakeRipResult(tempFile)]);

            List<VideoEncodeJob> dispatched = [];
            Mock<IJobDispatcher> dispatcherMock = new();
            dispatcherMock
                .Setup(d =>
                    d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>())
                )
                .Callback<IShouldQueue, string, int>(
                    (job, _, _) =>
                    {
                        if (job is VideoEncodeJob encodeJob)
                            dispatched.Add(encodeJob);
                    }
                );

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            // Folder has KnownPresetId but request asks for a different one
            Ulid differentPresetId = Ulid.NewUlid();
            Folder folder = MakeFolder([MakePresetLink(KnownPresetId)]);
            Library library = MakeLibrary();
            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");

            RipRequest request = MakeVideoRequest(
                discType: OpticalDiscType.BluRay,
                encodingProfileId: differentPresetId.ToString()
            );

            DiscRipJob job = BuildJob(
                request,
                ripperMock.Object,
                dispatcherMock.Object,
                storageMock.Object,
                folder,
                library
            );

            await job.Handle();

            dispatched.Should().HaveCount(1);
            dispatched[0].PresetId.Should().BeNull("unmatched profile id must fall back to null");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ── Dispatch with no profile id (folder default) ──────────────────────

    [Fact]
    public async Task Handle_VideoRipAndEncode_NullEncodingProfileId_DispatchesWithNullPresetId()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "title_03.mkv");
        await File.WriteAllBytesAsync(tempFile, []);

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
                .ReturnsAsync([MakeRipResult(tempFile)]);

            List<VideoEncodeJob> dispatched = [];
            Mock<IJobDispatcher> dispatcherMock = new();
            dispatcherMock
                .Setup(d =>
                    d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>())
                )
                .Callback<IShouldQueue, string, int>(
                    (job, _, _) =>
                    {
                        if (job is VideoEncodeJob encodeJob)
                            dispatched.Add(encodeJob);
                    }
                );

            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            Folder folder = MakeFolder([MakePresetLink(KnownPresetId)]);
            Library library = MakeLibrary();
            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");

            RipRequest request = MakeVideoRequest(
                discType: OpticalDiscType.Dvd,
                encodingProfileId: null
            );

            DiscRipJob job = BuildJob(
                request,
                ripperMock.Object,
                dispatcherMock.Object,
                storageMock.Object,
                folder,
                library
            );

            await job.Handle();

            dispatched.Should().HaveCount(1);
            dispatched[0].PresetId.Should().BeNull("absent profile id must produce null PresetId");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ── CD rip must NOT dispatch VideoEncodeJob ───────────────────────────

    [Fact]
    public async Task Handle_CdRipAndEncode_DoesNotDispatchVideoEncodeJob()
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
            .ReturnsAsync([]);

        List<IShouldQueue> dispatched = [];
        Mock<IJobDispatcher> dispatcherMock = new();
        dispatcherMock
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<IShouldQueue, string, int>((job, _, _) => dispatched.Add(job));

        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);

        Folder folder = MakeFolder();
        Library library = MakeLibrary();
        Mock<IStorage> storageMock = MakeStorageMock("/media/music");

        RipRequest request = MakeVideoRequest(discType: OpticalDiscType.Cd);

        DiscRipJob job = BuildJob(
            request,
            ripperMock.Object,
            dispatcherMock.Object,
            storageMock.Object,
            folder,
            library
        );

        await job.Handle();

        dispatched
            .Should()
            .NotContain(j => j is VideoEncodeJob, "CD rips must not trigger video encode");
    }

    // ── Dispatcher null falls back to FileCreatedEvent ───────────────────

    [Fact]
    public async Task Handle_VideoRipAndEncode_NullDispatcher_FallsBackToFileCreatedEvent()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "title_04.mkv");
        await File.WriteAllBytesAsync(tempFile, []);

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
                .ReturnsAsync([MakeRipResult(tempFile)]);

            List<object> publishedEvents = [];
            Mock<IEventBus> busMock = new();
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<DriveStateChangedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<DriveStateChangedEvent, CancellationToken>(
                    (evt, _) => publishedEvents.Add(evt)
                )
                .Returns(Task.CompletedTask);
            busMock
                .Setup(b =>
                    b.PublishAsync(
                        It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                        It.IsAny<CancellationToken>()
                    )
                )
                .Callback<NoMercy.Events.FileWatcher.FileCreatedEvent, CancellationToken>(
                    (evt, _) => publishedEvents.Add(evt)
                )
                .Returns(Task.CompletedTask);
            EventBusProvider.Configure(busMock.Object);

            Folder folder = MakeFolder([MakePresetLink(KnownPresetId)]);
            Library library = MakeLibrary();
            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");

            RipRequest request = MakeVideoRequest(discType: OpticalDiscType.Dvd);

            DiscRipJob job = BuildJob(
                request,
                ripperMock.Object,
                jobDispatcher: null,
                storageMock.Object,
                folder,
                library
            );

            await job.Handle();

            publishedEvents
                .OfType<NoMercy.Events.FileWatcher.FileCreatedEvent>()
                .Should()
                .HaveCount(1, "null dispatcher must fall back to FileCreatedEvent");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
