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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Shows;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Shows;

public class ShowManagerLibraryPlacementTests
{
    private static readonly DateTime RealFolderCreatedAt = new(
        2019,
        3,
        4,
        0,
        0,
        0,
        DateTimeKind.Utc
    );

    // Regression: a show whose folder physically exists under the library it
    // was scanned from must stay there - the folder is ground truth. This
    // reproduces the real bug: a title-match miss against AniList/Jikan
    // (verified live to happen for "Naruto Shippuden") must never evict a
    // show out of a dedicated anime library folder, because the folder
    // itself already proves where it belongs and no lookup is needed.
    [Fact]
    public async Task ResolveLibraryAndCreatedAtAsync_FolderExistsInAnimeLibrary_StaysNoMatterTheVerdict()
    {
        (Library animeLibrary, Mock<IStorageFactory> storageFactory) = BuildLibraryWithStorage(
            "anime",
            folderExists: true
        );
        ShowManager manager = BuildManager(storageFactory, animeLibraryLookup: null);

        (Library resolved, DateTime createdAt, bool folderDateIsReal) =
            await manager.ResolveLibraryAndCreatedAtAsync(
                id: 1,
                scannedLibrary: animeLibrary,
                baseUrl: "/n/Naruto.Shippuden.(2007)",
                mediaType: "tv" // the real live verdict AniList/Jikan give for this title
            );

        resolved.Should().BeSameAs(animeLibrary);
        createdAt.Should().Be(RealFolderCreatedAt);
        folderDateIsReal.Should().BeTrue();
    }

    // A show whose folder exists in the scanned library is never moved, even
    // when the classifier is inconclusive (null).
    [Fact]
    public async Task ResolveLibraryAndCreatedAtAsync_FolderExistsInAnimeLibrary_InconclusiveVerdictStaysToo()
    {
        (Library animeLibrary, Mock<IStorageFactory> storageFactory) = BuildLibraryWithStorage(
            "anime",
            folderExists: true
        );
        ShowManager manager = BuildManager(storageFactory, animeLibraryLookup: null);

        (Library resolved, DateTime createdAt, bool folderDateIsReal) =
            await manager.ResolveLibraryAndCreatedAtAsync(
                id: 1,
                scannedLibrary: animeLibrary,
                baseUrl: "/n/Naruto.Shippuden.(2007)",
                mediaType: null
            );

        resolved.Should().BeSameAs(animeLibrary);
        createdAt.Should().Be(RealFolderCreatedAt);
        folderDateIsReal.Should().BeTrue();
    }

    // The classifier only gets a say when the scanned library has NO on-disk
    // trace of the folder at all (a manually-added show with no file yet) -
    // and even then only promotes into anime when the anime library's own
    // folders can also prove the folder is there.
    [Fact]
    public async Task ResolveLibraryAndCreatedAtAsync_NoFolderInScannedLibrary_PromotesWhenAnimeLibraryHasIt()
    {
        (Library tvLibrary, Mock<IStorageFactory> tvStorageFactory) = BuildLibraryWithStorage(
            "tv",
            folderExists: false
        );
        (Library animeLibrary, Mock<IStorageFactory> animeStorageFactory) = BuildLibraryWithStorage(
            "anime",
            folderExists: true
        );

        Mock<IStorageFactory> combinedFactory = MergeStorageFactories(
            tvLibrary,
            tvStorageFactory,
            animeLibrary,
            animeStorageFactory
        );
        ShowManager manager = BuildManager(combinedFactory, animeLibraryLookup: animeLibrary);

        (Library resolved, DateTime createdAt, bool folderDateIsReal) =
            await manager.ResolveLibraryAndCreatedAtAsync(
                id: 2,
                scannedLibrary: tvLibrary,
                baseUrl: "/h/Hunter.x.Hunter.(2011)",
                mediaType: "anime"
            );

        resolved.Should().BeSameAs(animeLibrary);
        createdAt.Should().Be(RealFolderCreatedAt);
        folderDateIsReal.Should().BeTrue();
    }

    // Classifier says anime, but the anime library's own folders have no
    // trace of it either - conservative: stay put rather than move on a
    // guess with no structural backing.
    [Fact]
    public async Task ResolveLibraryAndCreatedAtAsync_NoFolderAnywhere_StaysInScannedLibrary()
    {
        (Library tvLibrary, Mock<IStorageFactory> tvStorageFactory) = BuildLibraryWithStorage(
            "tv",
            folderExists: false
        );
        (Library animeLibrary, Mock<IStorageFactory> animeStorageFactory) = BuildLibraryWithStorage(
            "anime",
            folderExists: false
        );

        Mock<IStorageFactory> combinedFactory = MergeStorageFactories(
            tvLibrary,
            tvStorageFactory,
            animeLibrary,
            animeStorageFactory
        );
        ShowManager manager = BuildManager(combinedFactory, animeLibraryLookup: animeLibrary);

        (Library resolved, DateTime _, bool folderDateIsReal) =
            await manager.ResolveLibraryAndCreatedAtAsync(
                id: 3,
                scannedLibrary: tvLibrary,
                baseUrl: "/g/Ghost.In.The.Shell.(1995)",
                mediaType: "anime"
            );

        resolved.Should().BeSameAs(tvLibrary);
        // No structural evidence anywhere: the repository must NOT be told
        // to stamp a "now" CreatedAt over whatever a previously-successful
        // scan already recorded for this row.
        folderDateIsReal.Should().BeFalse();
    }

    private static ShowManager BuildManager(
        Mock<IStorageFactory> storageFactory,
        Library? animeLibraryLookup
    )
    {
        Mock<IShowRepository> showRepository = new();
        showRepository
            .Setup(r => r.GetLibraryByTypeAsync("anime"))
            .ReturnsAsync(animeLibraryLookup);

        return new ShowManager(
            showRepository.Object,
            new JobDispatcher(),
            storageFactory.Object,
            Mock.Of<IMediaTypeClassifier>(),
            Mock.Of<IAnimeEnrichmentService>(),
            NullLogger<ShowManager>.Instance
        );
    }

    private static (Library library, Mock<IStorageFactory> storageFactory) BuildLibraryWithStorage(
        string libraryType,
        bool folderExists
    )
    {
        Ulid folderId = Ulid.NewUlid();
        Ulid driverId = Ulid.NewUlid();
        Ulid libraryId = Ulid.NewUlid();

        Mock<IStorageFactory> storageFactory = new();
        SetUpStorage(storageFactory, folderId, driverId, folderExists);

        Library library = new()
        {
            Id = libraryId,
            Type = libraryType,
            Title = libraryType,
            FolderLibraries =
            [
                new FolderLibrary(folderId, libraryId)
                {
                    Folder = new()
                    {
                        Id = folderId,
                        DriverId = driverId,
                        Path = "/root",
                    },
                },
            ],
        };

        return (library, storageFactory);
    }

    private static void SetUpStorage(
        Mock<IStorageFactory> storageFactory,
        Ulid folderId,
        Ulid driverId,
        bool folderExists
    )
    {
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.DirectoryExists(It.IsAny<string>())).Returns(false);
        driver.Setup(d => d.GetCreationTimeUtc(It.IsAny<string>())).Returns(RealFolderCreatedAt);

        Mock<IStorage> storage = new();
        storage.Setup(s => s.Driver).Returns(driver.Object);
        storage
            .Setup(s => s.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string parent, string[] child) => parent + "/" + string.Join("/", child));
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(folderExists);

        storageFactory.Setup(f => f.For(folderId, driverId, string.Empty)).Returns(storage.Object);
    }

    // Both libraries' storage factories were built independently; a real
    // IStorageFactory serves every folder, so tests that touch two libraries
    // need one factory whose For() dispatches to whichever mock owns the id.
    private static Mock<IStorageFactory> MergeStorageFactories(
        Library libraryA,
        Mock<IStorageFactory> factoryA,
        Library libraryB,
        Mock<IStorageFactory> factoryB
    )
    {
        Mock<IStorageFactory> merged = new();
        merged
            .Setup(f =>
                f.For(
                    libraryA.FolderLibraries!.First().FolderId,
                    libraryA.FolderLibraries!.First().Folder.DriverId,
                    string.Empty
                )
            )
            .Returns(
                factoryA.Object.For(
                    libraryA.FolderLibraries!.First().FolderId,
                    libraryA.FolderLibraries!.First().Folder.DriverId,
                    string.Empty
                )
            );
        merged
            .Setup(f =>
                f.For(
                    libraryB.FolderLibraries!.First().FolderId,
                    libraryB.FolderLibraries!.First().Folder.DriverId,
                    string.Empty
                )
            )
            .Returns(
                factoryB.Object.For(
                    libraryB.FolderLibraries!.First().FolderId,
                    libraryB.FolderLibraries!.First().Folder.DriverId,
                    string.Empty
                )
            );
        return merged;
    }
}
