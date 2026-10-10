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
using NoMercy.Events;
using NoMercy.Events.DriveMonitor;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;
using OpticalMediaType = NoMercy.OpticalMedia.Metadata.MediaType;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// Testable subclass that short-circuits the MediaContext DB fetch, mirroring
/// the pattern in DiscRipJobEncodeDispatchTests (that class is file-scoped
/// there, so it can't be reused across files).
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
/// REQUIREMENT: when a rip request supplies no <see cref="RipRequest.Custom"/>
/// metadata, <see cref="DiscRipJob.Handle"/> must build a synthetic
/// <see cref="DiscInfo"/> from the drive-path label and run it through
/// <see cref="DiscIdentificationService"/>. A high-confidence single
/// candidate auto-applies (the file is renamed/moved using the resolved
/// title); a lower-confidence candidate instead writes a
/// <c>pending_NN.json</c> sidecar and publishes a "pending" progress event
/// without moving the file; no candidate at all falls through to the
/// original (label-based) output naming.
/// </summary>
[Collection("EventBusProvider")]
[Trait("Category", "Unit")]
public class DiscRipJobAutoApplyTests
{
    private static readonly Ulid KnownFolderId = Ulid.NewUlid();
    private static readonly Ulid KnownLibraryId = Ulid.NewUlid();

    private static RipRequest MakeNoCustomRequest(string drivePath = "D:\\Inception") =>
        new(
            DrivePath: drivePath,
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: null,
            LibraryId: KnownLibraryId,
            FolderId: KnownFolderId,
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: RipMode.RipAndEncode,
            DiscType: OpticalDiscType.Dvd
        );

    private static Folder MakeFolder() =>
        new()
        {
            Id = KnownFolderId,
            Path = "/media/movies",
            EncodingPresetFolders = [],
        };

    private static Library MakeLibrary() =>
        new()
        {
            Id = KnownLibraryId,
            Title = "Movies",
            Type = "movie",
            FolderLibraries = [],
        };

    private static DiscRipResult MakeRipResult(string outputPath) =>
        new(
            TitleIndex: 1,
            OutputPath: outputPath,
            Success: true,
            Duration: TimeSpan.FromMinutes(100),
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
        DiscIdentificationService identificationService,
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
            TargetLibraryType = "movie",
            DiscRipper = ripper,
            IdentificationService = identificationService,
            StorageFactory = factoryMock.Object,
            StorageDriver = Mock.Of<IStorageDriver>(),
            DriveLockRegistry = new(),
            LoggerFactory = NullLoggerFactory.Instance,
            JobDispatcher = null,
        };

        return job;
    }

    private static Mock<IEventBus> ConfigureEventBus(List<object> published)
    {
        Mock<IEventBus> busMock = new();
        busMock
            .Setup(b =>
                b.PublishAsync(It.IsAny<DriveStateChangedEvent>(), It.IsAny<CancellationToken>())
            )
            .Callback<DriveStateChangedEvent, CancellationToken>((evt, _) => published.Add(evt))
            .Returns(Task.CompletedTask);
        busMock
            .Setup(b =>
                b.PublishAsync(
                    It.IsAny<NoMercy.Events.FileWatcher.FileCreatedEvent>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<NoMercy.Events.FileWatcher.FileCreatedEvent, CancellationToken>(
                (evt, _) => published.Add(evt)
            )
            .Returns(Task.CompletedTask);
        EventBusProvider.Configure(busMock.Object);
        return busMock;
    }

    [Fact]
    public async Task Handle_NoCustomMetadata_HighConfidenceCandidate_AutoAppliesAndDispatches()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"autoapply_{Guid.NewGuid():N}.mkv");
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

            Mock<IDiscIdentifier> identifierMock = new();
            identifierMock.Setup(i => i.CanHandle(It.IsAny<OpticalDiscType>())).Returns(true);
            identifierMock
                .Setup(i => i.IdentifyAsync(It.IsAny<DiscInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    new DiscIdentification(
                        Kind: MediaKind.Movie,
                        Candidates:
                        [
                            new(
                                Source: "tmdb",
                                StableId: "27205",
                                Title: "Inception",
                                Year: 2010,
                                PosterUrl: null,
                                BackdropUrl: null,
                                Confidence: 0.95,
                                Type: OpticalMediaType.Movie
                            ),
                        ],
                        TopConfidence: 0.95,
                        AutoApply: true,
                        NeedsManualAssignment: false
                    )
                );

            DiscIdentificationService identificationService = new(
                [identifierMock.Object],
                NullLogger<DiscIdentificationService>.Instance
            );

            List<object> published = [];
            ConfigureEventBus(published);

            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");
            DiscRipJob job = BuildJob(
                MakeNoCustomRequest(),
                ripperMock.Object,
                identificationService,
                storageMock.Object
            );

            await job.Handle();

            List<string> methods = published
                .OfType<DriveStateChangedEvent>()
                .Select(e => e.DriveStateData.Method)
                .ToList();
            methods.Should().Contain("rip_complete");
            methods.Should().NotContain("rip_pending");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Handle_NoCustomMetadata_LowConfidenceCandidate_WritesPendingJsonAndSkipsMove()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"pending_{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(tempFile, []);
        string outputDir = Path.Combine(Path.GetTempPath(), $"rip_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
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

            Mock<IDiscIdentifier> identifierMock = new();
            identifierMock.Setup(i => i.CanHandle(It.IsAny<OpticalDiscType>())).Returns(true);
            identifierMock
                .Setup(i => i.IdentifyAsync(It.IsAny<DiscInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                    new DiscIdentification(
                        Kind: MediaKind.Movie,
                        Candidates:
                        [
                            new(
                                Source: "tmdb",
                                StableId: "27205",
                                Title: "Maybe Inception",
                                Year: 2010,
                                PosterUrl: null,
                                BackdropUrl: null,
                                Confidence: 0.4,
                                Type: OpticalMediaType.Movie
                            ),
                        ],
                        TopConfidence: 0.4,
                        AutoApply: false,
                        NeedsManualAssignment: false
                    )
                );

            DiscIdentificationService identificationService = new(
                [identifierMock.Object],
                NullLogger<DiscIdentificationService>.Instance
            );

            List<object> published = [];
            ConfigureEventBus(published);

            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");
            RipRequest request = MakeNoCustomRequest() with { SelectedTitleIndices = [1] };
            Mock<IStorageFactory> factoryMock = new();
            factoryMock
                .Setup(f => f.For(It.IsAny<Ulid>(), It.IsAny<Ulid>(), It.IsAny<string>()))
                .Returns(storageMock.Object);

            TestableDiscRipJob job = new(MakeFolder(), MakeLibrary())
            {
                Request = request,
                OutputDir = outputDir,
                TargetFolderId = KnownFolderId,
                TargetLibraryId = KnownLibraryId,
                TargetLibraryType = "movie",
                DiscRipper = ripperMock.Object,
                IdentificationService = identificationService,
                StorageFactory = factoryMock.Object,
                StorageDriver = Mock.Of<IStorageDriver>(),
                DriveLockRegistry = new(),
                LoggerFactory = NullLoggerFactory.Instance,
                JobDispatcher = null,
            };

            await job.Handle();

            published
                .OfType<DriveStateChangedEvent>()
                .Select(e => e.DriveStateData.Method)
                .Should()
                .Contain("rip_pending");

            string pendingPath = Path.Combine(outputDir, "pending_01.json");
            File.Exists(pendingPath)
                .Should()
                .BeTrue("a low-confidence match must be saved for manual confirmation");
            string json = await File.ReadAllTextAsync(pendingPath);
            json.Should().Contain("Maybe Inception");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Handle_NoCustomMetadata_NoCandidatesFound_FallsBackToLabelBasedNaming()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"nomatch_{Guid.NewGuid():N}.mkv");
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

            DiscIdentificationService identificationService = new(
                [],
                NullLogger<DiscIdentificationService>.Instance
            );

            List<object> published = [];
            ConfigureEventBus(published);

            Mock<IStorage> storageMock = MakeStorageMock("/media/movies");
            DiscRipJob job = BuildJob(
                MakeNoCustomRequest(),
                ripperMock.Object,
                identificationService,
                storageMock.Object
            );

            await job.Handle();

            published
                .OfType<DriveStateChangedEvent>()
                .Select(e => e.DriveStateData.Method)
                .Should()
                .Contain("rip_complete");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
