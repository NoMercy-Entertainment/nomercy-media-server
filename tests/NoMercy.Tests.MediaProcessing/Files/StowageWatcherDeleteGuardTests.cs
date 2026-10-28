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
using NoMercy.MediaProcessing.Files;
using Stowage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A listing that comes back empty or short because a share went away is not a mass
/// delete. The watcher keeps its snapshot and reports no Deleted event for it.
/// </summary>
[Trait("Category", "Unit")]
public class StowageWatcherDeleteGuardTests
{
    private static IReadOnlyCollection<IOEntry> Files(int count) =>
        [.. Enumerable.Range(1, count).Select(n => new IOEntry($"/file{n:000}.mkv"))];

    private static async Task<List<StowageWatcherEventArgs>> DeletesAfterSeedAsync(
        IReadOnlyCollection<IOEntry> seed,
        IReadOnlyCollection<IOEntry> next
    )
    {
        Mock<IFileStorage> storage = new();
        int calls = 0;
        storage
            .Setup(s => s.Ls(It.IsAny<IOPath?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Interlocked.Increment(ref calls) == 1 ? seed : next));

        List<StowageWatcherEventArgs> deletes = [];
        StowageWatcher watcher = new(storage.Object, "/");
        watcher.Deleted += args =>
        {
            lock (deletes)
                deletes.Add(args);
        };

        watcher.Watch(TimeSpan.FromMilliseconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        watcher.Dispose();

        lock (deletes)
            return [.. deletes];
    }

    [Fact]
    public async Task An_empty_listing_after_a_full_one_reports_no_deletes()
    {
        List<StowageWatcherEventArgs> deletes = await DeletesAfterSeedAsync(Files(30), []);

        deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_listing_that_lost_most_files_reports_no_deletes()
    {
        List<StowageWatcherEventArgs> deletes = await DeletesAfterSeedAsync(Files(40), Files(10));

        deletes.Should().BeEmpty();
    }

    [Fact]
    public async Task One_file_gone_from_a_full_listing_is_still_reported()
    {
        List<StowageWatcherEventArgs> deletes = await DeletesAfterSeedAsync(Files(40), Files(39));

        deletes.Should().ContainSingle().Which.Path.Should().Be("/file040.mkv");
    }
}
