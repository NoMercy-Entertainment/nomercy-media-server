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
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// FolderWatcher keeps its live watchers in a process-static set. Watch() is called
/// again whenever a library is added/refreshed/rescanned; if that stacked a second
/// watcher on a folder already watched, it leaked OS handles and threads and fired
/// every file event twice → duplicate scan/encode jobs. Re-watching must replace,
/// not stack.
/// </summary>
public class FolderWatcherDedupTests
{
    [Fact]
    public void Watch_SameFolderTwice_KeepsOneWatcherNotStacked()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "nm-fw-dedup-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);

        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.DirectoryExists(It.IsAny<string>())).Returns(true);

        FolderWatcher watcher = new(driver.Object);
        try
        {
            // Within-assembly parallelism is disabled, so this clears the static set
            // of any watcher another test left behind before we measure.
            watcher.Dispose();

            watcher.Watch([dir]);
            int afterFirst = FolderWatcher.WatcherCount;

            watcher.Watch([dir]);
            int afterSecond = FolderWatcher.WatcherCount;

            Assert.Equal(1, afterFirst);
            Assert.Equal(1, afterSecond);
        }
        finally
        {
            watcher.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
