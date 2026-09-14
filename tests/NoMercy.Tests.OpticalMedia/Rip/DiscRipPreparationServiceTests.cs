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
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Storage;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// <see cref="DiscRipPreparationService"/> replaces OpticalMediaController.RipDisc's own
/// inline DRM precheck, <c>new LibraryRepository(...)</c> destination validation, and
/// <see cref="Directory.CreateDirectory"/> call. Every failure branch here maps to a 400
/// Bad Request in the controller, so these tests assert on <c>Success</c>/<c>ErrorMessage</c>
/// rather than any HTTP-specific shape.
/// </summary>
[Trait("Category", "Unit")]
public class DiscRipPreparationServiceTests
{
    private static DiscDrive MakeDrive(OpticalDiscType discType = OpticalDiscType.Dvd) =>
        new("D:\\", "TEST", HasDisc: true, discType);

    private static RipRequest MakeRequest(
        RipMode mode = RipMode.RipAndEncode,
        int[]? selectedTitleIndices = null
    ) =>
        new(
            DrivePath: "D:\\",
            SelectedTitleIndices: selectedTitleIndices ?? [0],
            MetadataId: null,
            Custom: null,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: [],
            Mode: mode
        );

    private static DiscInfo MakeProbe(
        DiscProtection? protection = null,
        DiscTrack[]? audio = null
    ) => new(OpticalDiscType.Dvd, "TEST", [], audio, TimeSpan.FromMinutes(90), protection);

    private static DiscSourceFactory MakeSourceFactory(IDiscSource source) => new([source]);

    [Fact]
    public async Task PrepareAsync_DrmProtectedDisc_Fails()
    {
        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe(new DiscProtection("AACS", null, "keys missing")));

        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IStorageDriver>()
        );

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(),
            MakeRequest(),
            CancellationToken.None
        );

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("AACS-protected");
    }

    [Fact]
    public async Task PrepareAsync_RipAndEncodeWithUnknownFolder_Fails()
    {
        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe());

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(r => r.GetLibraryFolder(It.IsAny<Ulid>()))
            .ReturnsAsync((Folder?)null);

        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            libraryRepository.Object,
            Mock.Of<IStorageDriver>()
        );

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(),
            MakeRequest(),
            CancellationToken.None
        );

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not match any library folder");
    }

    [Fact]
    public async Task PrepareAsync_RipAndEncodeWithUnknownLibrary_Fails()
    {
        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe());

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository
            .Setup(r => r.GetLibraryFolder(It.IsAny<Ulid>()))
            .ReturnsAsync(new Folder { Id = Ulid.NewUlid(), Path = "/media" });
        libraryRepository
            .Setup(r => r.GetLibraryByIdWithFolders(It.IsAny<Ulid>()))
            .ReturnsAsync((Library?)null);

        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            libraryRepository.Object,
            Mock.Of<IStorageDriver>()
        );

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(),
            MakeRequest(),
            CancellationToken.None
        );

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("does not match any library");
    }

    [Fact]
    public async Task PrepareAsync_ValidRipAndEncode_ReturnsEnrichedRequestAndTargets()
    {
        Ulid folderId = Ulid.NewUlid();
        Ulid libraryId = Ulid.NewUlid();
        Folder folder = new() { Id = folderId, Path = "/media" };
        Library library = new()
        {
            Id = libraryId,
            Title = "Movies",
            Type = "movie",
        };

        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe());

        Mock<ILibraryRepository> libraryRepository = new();
        libraryRepository.Setup(r => r.GetLibraryFolder(folderId)).ReturnsAsync(folder);
        libraryRepository.Setup(r => r.GetLibraryByIdWithFolders(libraryId)).ReturnsAsync(library);

        Mock<IStorageDriver> storageDriver = new();

        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            libraryRepository.Object,
            storageDriver.Object
        );

        RipRequest request = MakeRequest() with { FolderId = folderId, LibraryId = libraryId };

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(),
            request,
            CancellationToken.None
        );

        result.Success.Should().BeTrue();
        result.TargetFolderId.Should().Be(folderId);
        result.TargetLibraryId.Should().Be(libraryId);
        result.TargetLibraryType.Should().Be("movie");
        result.EnrichedRequest!.DiscType.Should().Be(OpticalDiscType.Dvd);
        result.OutputDir.Should().NotBeNullOrEmpty();
        storageDriver.Verify(d => d.CreateDirectory(result.OutputDir!), Times.Once);
    }

    [Fact]
    public async Task PrepareAsync_RipToRaw_SkipsDestinationValidation()
    {
        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Dvd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe());

        // Deliberately never set up GetLibraryFolder/GetLibraryByIdWithFolders — a call would
        // return Moq's default null via Mock.Of, which the RipAndEncode branch would reject;
        // RipToRaw must never call them.
        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IStorageDriver>()
        );

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(),
            MakeRequest(mode: RipMode.RipToRaw),
            CancellationToken.None
        );

        result.Success.Should().BeTrue();
        result.TargetFolderId.Should().BeNull();
        result.TargetLibraryId.Should().BeNull();
    }

    [Fact]
    public async Task PrepareAsync_CdWithNoSelectedTitles_DefaultsToAllProbedAudioTracks()
    {
        DiscTrack[] tracks =
        [
            new(0, "Track 1", null, TimeSpan.FromMinutes(3), 44100, 2),
            new(1, "Track 2", null, TimeSpan.FromMinutes(4), 44100, 2),
        ];

        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(OpticalDiscType.Cd);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeProbe(audio: tracks));

        DiscRipPreparationService service = new(
            MakeSourceFactory(source.Object),
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IStorageDriver>()
        );

        DiscRipPreparationResult result = await service.PrepareAsync(
            MakeDrive(OpticalDiscType.Cd),
            MakeRequest(mode: RipMode.RipToRaw, selectedTitleIndices: []),
            CancellationToken.None
        );

        result.Success.Should().BeTrue();
        result.EnrichedRequest!.SelectedTitleIndices.Should().BeEquivalentTo([0, 1]);
    }
}
