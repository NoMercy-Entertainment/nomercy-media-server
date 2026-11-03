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
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Providers.AcoustId;
using NoMercy.Storage;
using NoMercy.Tests.MediaProcessing.Music;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// Issue #279: the music jobs stored every track under the library's first folder,
/// whichever folder the job was dispatched for, so a release in a library's second
/// folder got track rows and an encode pointing at a folder the file is not in.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicFolderJobDestinationFolderTests
{
    private readonly Folder _first = LibraryFolderResolverTests.FolderAt("/srv/music");
    private readonly Folder _second = LibraryFolderResolverTests.FolderAt("/srv/music2");

    private Library TwoFolderLibrary() =>
        LibraryFolderResolverTests.LibraryWithFolders(_first, _second);

    private IStorageFactory Roots() =>
        LibraryFolderResolverTests.FactoryResolving(
            (_first, "/srv/music"),
            (_second, "/srv/music2")
        );

    [Fact]
    public void A_repair_dispatched_for_the_second_folder_stores_under_the_second_folder()
    {
        MusicTrackRepairJob job = Job(new Mock<IStorageFactory>().Object);
        job.FolderId = _second.Id;
        job.InputFolder = "/srv/music2/A/ABBA/[1976] Arrival";

        job.ResolveDestinationFolder(TwoFolderLibrary())
            .Should()
            .BeSameAs(_second, "the job was dispatched for the second folder");
    }

    [Fact]
    public void A_repair_queued_without_a_folder_uses_the_folder_that_holds_its_input()
    {
        MusicTrackRepairJob job = Job(Roots());
        job.InputFolder = "/srv/music2/A/ABBA/[1976] Arrival";

        job.ResolveDestinationFolder(TwoFolderLibrary())
            .Should()
            .BeSameAs(_second, "an old payload carries no folder, so its path decides");
    }

    [Fact]
    public void An_import_dispatched_for_the_second_folder_stores_under_the_second_folder()
    {
        AudioImportJob job = new(
            new Mock<IStorageFactory>().Object,
            new Mock<IStorageDriver>().Object,
            new Mock<IAudioFingerprinter>().Object,
            NullLoggerFactory.Instance
        )
        {
            FolderId = _second.Id,
            InputFolder = "/downloads/complete/ABBA - Arrival",
        };

        job.ResolveDestinationFolder(TwoFolderLibrary()).Should().BeSameAs(_second);
    }

    [Fact]
    public void An_import_without_a_folder_from_outside_the_library_uses_the_first_folder()
    {
        AudioImportJob job = new(
            Roots(),
            new Mock<IStorageDriver>().Object,
            new Mock<IAudioFingerprinter>().Object,
            NullLoggerFactory.Instance
        )
        {
            InputFolder = "/downloads/complete/ABBA - Arrival",
        };

        job.ResolveDestinationFolder(TwoFolderLibrary()).Should().BeSameAs(_first);
    }

    private static MusicTrackRepairJob Job(IStorageFactory storageFactory) =>
        new(
            storageFactory,
            new Mock<IStorageDriver>().Object,
            new Mock<IAudioFingerprinter>().Object,
            NullLoggerFactory.Instance
        );
}
