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

using System.Collections.Concurrent;
using FluentAssertions;
using NoMercy.Data.Jobs;
using NoMercy.Database.Models.Media;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// StorageJob.Handle() used to mutate a shared Usage object's Movies/Shows/
/// Music/Other/Used properties via `+=` from inside a Parallel.ForEachAsync
/// body. That compound assignment is a non-atomic read-modify-write; when two
/// libraries resolve to the same folder path (a realistic case — the same
/// physical folder added to more than one library) concurrent writers lose
/// updates. The fix routes every contribution through a
/// ConcurrentDictionary&lt;path, StorageUsageDelta&gt; (plain long addition —
/// commutative regardless of scheduling) and applies it to Usage exactly once,
/// single-threaded, after the parallel phase completes.
/// </summary>
public class StorageJobUsageTests
{
    private static Metadata MakeMovieMetadata(
        string hostFolder,
        long videoFileSize,
        long folderSize
    ) =>
        new()
        {
            Type = MediaType.Movie,
            HostFolder = hostFolder,
            FolderSize = folderSize,
            Video = [new() { FileSize = videoFileSize }],
        };

    private static Metadata MakeTvMetadata(
        string hostFolder,
        long videoFileSize,
        long folderSize
    ) =>
        new()
        {
            Type = MediaType.Tv,
            HostFolder = hostFolder,
            FolderSize = folderSize,
            Video = [new() { FileSize = videoFileSize }],
        };

    private static Metadata MakeAlbumMetadata(
        string hostFolder,
        long audioFileSize,
        long folderSize
    ) =>
        new()
        {
            Type = MediaType.Music,
            HostFolder = hostFolder,
            FolderSize = folderSize,
            Audio = [new() { FileSize = audioFileSize }],
        };

    [Fact]
    public void ComputeFolderUsageDelta_SumsOnlyMetadataUnderTheFolderPath()
    {
        const string folder = "/media/movies/one";

        List<Metadata?> movieMetaData =
        [
            MakeMovieMetadata(folder, videoFileSize: 1000, folderSize: 1200),
            MakeMovieMetadata("/media/movies/two", videoFileSize: 9999, folderSize: 9999),
        ];

        StorageUsageDelta delta = StorageJob.ComputeFolderUsageDelta(
            folder,
            movieMetaData,
            tvMetaData: [],
            albumMetaData: []
        );

        delta.Movies.Should().Be(1000);
        delta.Used.Should().Be(1200);
        delta.Other.Should().Be(200);
        delta.Shows.Should().Be(0);
        delta.Music.Should().Be(0);
    }

    [Fact]
    public void ComputeFolderUsageDelta_AggregatesAcrossMovieTvAndAlbumMatches()
    {
        const string folder = "/media/mixed";

        List<Metadata?> movieMetaData = [MakeMovieMetadata(folder, 100, 120)];
        List<Metadata?> tvMetaData = [MakeTvMetadata(folder, 200, 240)];
        List<Metadata?> albumMetaData = [MakeAlbumMetadata(folder, 50, 60)];

        StorageUsageDelta delta = StorageJob.ComputeFolderUsageDelta(
            folder,
            movieMetaData,
            tvMetaData,
            albumMetaData
        );

        delta.Movies.Should().Be(100);
        delta.Shows.Should().Be(200);
        delta.Music.Should().Be(50);
        delta.Used.Should().Be(120 + 240 + 60);
        delta.Other.Should().Be(20 + 40 + 10);
    }

    [Fact]
    public void StorageUsageDelta_Add_IsCommutative()
    {
        StorageUsageDelta a = new(Movies: 10, Shows: 20, Music: 30, Other: 40, Used: 50);
        StorageUsageDelta b = new(Movies: 1, Shows: 2, Music: 3, Other: 4, Used: 5);

        StorageUsageDelta ab = a.Add(b);
        StorageUsageDelta ba = b.Add(a);

        ab.Should().Be(ba);
        ab.Used.Should().Be(55);
    }

    /// <summary>
    /// Reproduces the concurrency shape of StorageJob.Handle(): many "library"
    /// tasks running in parallel all resolve to the SAME folder path and merge
    /// their delta into the shared map via AddOrUpdate — the same call the
    /// fixed Handle() makes from inside Parallel.ForEachAsync. Asserts the
    /// final total is the exact deterministic sum with no lost updates, which
    /// is what the un-synchronized `storage.Data.Used += ...` could not
    /// guarantee.
    /// </summary>
    [Fact]
    public async Task ConcurrentMerge_IntoSharedPath_LosesNoUpdates()
    {
        const string folder = "/media/shared";
        const int concurrentLibraries = 200;
        ConcurrentDictionary<string, StorageUsageDelta> deltas = new();

        IEnumerable<Task> tasks = Enumerable
            .Range(0, concurrentLibraries)
            .Select(i =>
                Task.Run(() =>
                {
                    StorageUsageDelta delta = new(Movies: 1, Shows: 2, Music: 3, Other: 4, Used: 5);
                    deltas.AddOrUpdate(folder, delta, (_, existing) => existing.Add(delta));
                })
            );

        await Task.WhenAll(tasks);

        deltas.Should().ContainKey(folder);
        StorageUsageDelta total = deltas[folder];
        total.Movies.Should().Be(1 * concurrentLibraries);
        total.Shows.Should().Be(2 * concurrentLibraries);
        total.Music.Should().Be(3 * concurrentLibraries);
        total.Other.Should().Be(4 * concurrentLibraries);
        total.Used.Should().Be(5 * concurrentLibraries);
    }

    /// <summary>
    /// Same race, but across DISTINCT folder paths (the common case — each
    /// library normally owns its own folders). Confirms per-key isolation:
    /// concurrent writers to different keys never interfere with each other's
    /// totals.
    /// </summary>
    [Fact]
    public async Task ConcurrentMerge_AcrossDistinctPaths_KeepsTotalsIsolated()
    {
        const int folderCount = 20;
        const int contributionsPerFolder = 50;
        ConcurrentDictionary<string, StorageUsageDelta> deltas = new();

        IEnumerable<Task> tasks = Enumerable
            .Range(0, folderCount * contributionsPerFolder)
            .Select(i =>
                Task.Run(() =>
                {
                    string path = $"/media/folder-{i % folderCount}";
                    StorageUsageDelta delta = new(Movies: 0, Shows: 0, Music: 0, Other: 0, Used: 7);
                    deltas.AddOrUpdate(path, delta, (_, existing) => existing.Add(delta));
                })
            );

        await Task.WhenAll(tasks);

        deltas.Should().HaveCount(folderCount);
        foreach (KeyValuePair<string, StorageUsageDelta> entry in deltas)
            entry.Value.Used.Should().Be(7 * contributionsPerFolder);
    }
}
