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

using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A burst of file actions in one folder is debounced into one group; the group keeps every
/// distinct action, not only the last one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileChangeGroupTests
{
    private const string Folder = "/mnt/media/Music/Synthetic Artist/Album";

    private static FileChangeGroup NewGroup() => new(new Library(), Folder);

    [Fact]
    public void A_burst_of_renames_keeps_every_rename()
    {
        FileChangeGroup group = NewGroup();

        group.Record(WatcherChangeTypes.Renamed, $"{Folder}/01 New.flac", $"{Folder}/01 Old.flac");
        group.Record(WatcherChangeTypes.Renamed, $"{Folder}/02 New.flac", $"{Folder}/02 Old.flac");

        group
            .Changes.Select(change => (change.OldFullPath, change.FullPath))
            .Should()
            .Equal(
                ($"{Folder}/01 Old.flac", $"{Folder}/01 New.flac"),
                ($"{Folder}/02 Old.flac", $"{Folder}/02 New.flac")
            );
    }

    [Fact]
    public void A_burst_of_deletes_keeps_every_delete()
    {
        FileChangeGroup group = NewGroup();

        group.Record(WatcherChangeTypes.Deleted, $"{Folder}/01.flac", null);
        group.Record(WatcherChangeTypes.Deleted, $"{Folder}/02.flac", null);

        group.Changes.Select(change => change.FullPath).Should().Equal($"{Folder}/01.flac", $"{Folder}/02.flac");
    }

    [Fact]
    public void The_same_path_twice_keeps_only_its_latest_action()
    {
        FileChangeGroup group = NewGroup();

        group.Record(WatcherChangeTypes.Created, $"{Folder}/01.flac", null);
        group.Record(WatcherChangeTypes.Changed, $"{Folder}/01.flac", null);

        group.Changes.Should().ContainSingle().Which.ChangeType.Should().Be(WatcherChangeTypes.Changed);
    }

    [Fact]
    public void Deleting_the_target_of_a_rename_still_deletes_the_source()
    {
        FileChangeGroup group = NewGroup();

        group.Record(WatcherChangeTypes.Renamed, $"{Folder}/01 New.flac", $"{Folder}/01 Old.flac");
        group.Record(WatcherChangeTypes.Deleted, $"{Folder}/01 New.flac", null);

        group
            .Changes.Select(change => (change.ChangeType, change.FullPath))
            .Should()
            .Equal(
                (WatcherChangeTypes.Deleted, $"{Folder}/01 Old.flac"),
                (WatcherChangeTypes.Deleted, $"{Folder}/01 New.flac")
            );
    }
}
