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

namespace NoMercy.Tests.Storage;

[Trait("Category", "Unit")]
public class StoragePathHelpersLibraryRelativeTests
{
    [Fact]
    public void Splits_a_driver_absolute_path_into_the_pair_a_media_row_persists()
    {
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            "/mnt/vault/Media/Libraries/Music/U2/The Joshua Tree/01. Where The Streets Have No Name.flac",
            "Libraries/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeTrue();
        folder.Should().Be("/U2/The Joshua Tree");
        filename.Should().Be("/01. Where The Streets Have No Name.flac");
    }

    [Fact]
    public void Rejects_a_file_that_does_not_live_under_the_library_root()
    {
        // The 11 rows found in the live dev database: a download folder on
        // another drive whose path the library root never matched, stored raw.
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            "M:/Download/complete/U2 - The Joshua Tree (1987) [24-48] FLAC 88/01. Where The Streets Have No Name.flac",
            "Libraries/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeFalse();
        folder.Should().BeEmpty();
        filename.Should().BeEmpty();
    }

    [Fact]
    public void Rejects_an_empty_path_instead_of_yielding_a_bare_separator()
    {
        // The reported record: both halves collapsed, so the composed URL was
        // /{FolderId}/ and could never resolve to a file.
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            string.Empty,
            "Libraries/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeFalse();
        folder.Should().BeEmpty();
        filename.Should().BeEmpty();
    }

    [Fact]
    public void Rejects_a_path_that_names_a_directory_rather_than_a_file()
    {
        StoragePathHelpers
            .TryGetLibraryRelativeParts(
                "/mnt/vault/Media/Libraries/Music/U2/The Joshua Tree/",
                "Libraries/Music",
                out _,
                out _
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Rejects_an_empty_library_root_because_nothing_can_be_made_relative_to_it()
    {
        StoragePathHelpers
            .TryGetLibraryRelativeParts(
                "/mnt/vault/Media/Libraries/Music/U2/x.flac",
                string.Empty,
                out _,
                out _
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Yields_an_empty_folder_for_a_file_sitting_directly_in_the_library_root()
    {
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            "/mnt/vault/Media/Libraries/Music/loose.flac",
            "Libraries/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeTrue();
        folder.Should().BeEmpty();
        filename.Should().Be("/loose.flac");
    }

    [Fact]
    public void Normalizes_windows_backslashes_in_both_the_path_and_the_root()
    {
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            @"M:\Media\Libraries\Music\U2\The Joshua Tree\01. Where.flac",
            @"Libraries\Music",
            out string folder,
            out string filename
        );

        ok.Should().BeTrue();
        folder.Should().Be("/U2/The Joshua Tree");
        filename.Should().Be("/01. Where.flac");
    }

    [Fact]
    public void Matches_the_library_root_case_insensitively()
    {
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            "/mnt/vault/Media/LIBRARIES/music/U2/x.flac",
            "Libraries/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeTrue();
        folder.Should().Be("/U2");
        filename.Should().Be("/x.flac");
    }

    [Fact]
    public void Rejects_a_sibling_folder_whose_name_only_starts_with_the_root_name()
    {
        // Issue #500: an album in "Music Unsorted" matched the root "Music" mid-segment,
        // so the relative folder became "/ Unsorted/..." and the move source was rebuilt
        // from it, a path that never existed.
        bool ok = StoragePathHelpers.TryGetLibraryRelativeParts(
            "Y:/media/Music Unsorted/batch/01 Track.flac",
            "Y:/media/Music",
            out string folder,
            out string filename
        );

        ok.Should().BeFalse();
        folder.Should().BeEmpty();
        filename.Should().BeEmpty();
    }

    [Fact]
    public void Rejects_a_folder_that_only_ends_with_the_root_name()
    {
        StoragePathHelpers
            .TryGetLibraryRelativeFolder("/mnt/vault/MusicArchive/U2", "Music", out string folder)
            .Should()
            .BeFalse();
        folder.Should().BeEmpty();
    }

    [Fact]
    public void Finds_the_whole_segment_root_after_an_earlier_partial_match()
    {
        bool ok = StoragePathHelpers.TryGetLibraryRelativeFolder(
            "Y:/media/Music Unsorted/Music/U2",
            "Music",
            out string folder
        );

        ok.Should().BeTrue();
        folder.Should().Be("/U2");
    }
}
