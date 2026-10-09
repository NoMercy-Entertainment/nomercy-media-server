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
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.MediaProcessing.Jobs.MediaJobs;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// Issue #500: a manual music import names a release and a destination folder, but
/// ReleaseImportJob dropped the release id when it handed the album to AudioImportJob,
/// and AudioImportJob never read the folder id (it always took the library's first
/// folder). The operator's choice was ignored on both counts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AudioImportChosenReleaseAndFolderTests
{
    private static Library LibraryWithFolders(params Folder[] folders)
    {
        Library library = new() { Id = Ulid.NewUlid() };
        foreach (Folder folder in folders)
            library.FolderLibraries.Add(
                new(folder.Id, library.Id) { Folder = folder, Library = library }
            );
        return library;
    }

    private static Folder FolderAt(string path) =>
        new()
        {
            Id = Ulid.NewUlid(),
            DriverId = Ulid.NewUlid(),
            Path = path,
        };

    [Fact]
    public void A_chosen_release_replaces_the_release_named_in_the_file_tags()
    {
        Guid chosen = Guid.NewGuid();
        AudioTagModel tag = new() { MusicBrainz = new() { ReleaseId = Guid.NewGuid() } };

        Guid? result = AudioImportJob.ApplyExplicitRelease(chosen, tag);

        result.Should().Be(chosen);
        tag.MusicBrainz!.ReleaseId.Should().Be(chosen);
    }

    [Fact]
    public void A_chosen_release_is_applied_to_a_file_with_no_musicbrainz_tags()
    {
        Guid chosen = Guid.NewGuid();
        AudioTagModel tag = new() { MusicBrainz = null };

        Guid? result = AudioImportJob.ApplyExplicitRelease(chosen, tag);

        result.Should().Be(chosen);
        tag.MusicBrainz.Should().NotBeNull();
        tag.MusicBrainz!.ReleaseId.Should().Be(chosen);
    }

    [Fact]
    public void No_chosen_release_leaves_the_tag_resolution_alone()
    {
        Guid tagged = Guid.NewGuid();
        AudioTagModel tag = new() { MusicBrainz = new() { ReleaseId = tagged } };

        Guid? result = AudioImportJob.ApplyExplicitRelease(Guid.Empty, tag);

        result.Should().BeNull("an empty release id means nothing was chosen");
        tag.MusicBrainz!.ReleaseId.Should().Be(tagged);
    }

    [Fact]
    public void The_chosen_folder_is_used_when_it_belongs_to_the_library()
    {
        Folder first = FolderAt("Music");
        Folder second = FolderAt("Music2");
        Library library = LibraryWithFolders(first, second);

        AudioImportJob.SelectDestinationFolder(library, second.Id).Should().BeSameAs(second);
    }

    [Fact]
    public void An_unset_folder_falls_back_to_the_first_library_folder()
    {
        Folder first = FolderAt("Music");
        Folder second = FolderAt("Music2");
        Library library = LibraryWithFolders(first, second);

        AudioImportJob.SelectDestinationFolder(library, default).Should().BeSameAs(first);
    }

    [Fact]
    public void A_folder_outside_the_library_falls_back_to_the_first_library_folder()
    {
        Folder first = FolderAt("Music");
        Library library = LibraryWithFolders(first, FolderAt("Music2"));

        AudioImportJob.SelectDestinationFolder(library, Ulid.NewUlid()).Should().BeSameAs(first);
    }

    [Fact]
    public void ReleaseImportJob_hands_the_chosen_release_and_folder_to_AudioImportJob()
    {
        Ulid libraryId = Ulid.NewUlid();
        Guid releaseId = Guid.NewGuid();
        Folder destination = FolderAt("Music");
        ReleaseImportJob job = new()
        {
            LibraryId = libraryId,
            FolderId = destination.Id,
            ReleaseId = releaseId,
            InputFolder = "unused",
        };
        Mock<JobDispatcher> dispatcher = new();

        job.DispatchAudioImport(dispatcher.Object, destination, "Y:/staging/Album");

        dispatcher.Verify(
            d =>
                d.DispatchJob<AudioImportJob>(
                    libraryId,
                    destination.Id,
                    releaseId,
                    "Y:/staging/Album"
                ),
            Times.Once
        );
    }
}
