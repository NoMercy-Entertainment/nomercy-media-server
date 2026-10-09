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

using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// After a FileSystemWatcher raises an error (e.g. InternalBufferOverflow) it stops
/// raising events; the watched folder was going permanently blind because the error
/// handler never re-armed it. TryReArm restores event delivery.
/// </summary>
public class FolderWatcherReArmTests
{
    [Fact]
    public void TryReArm_ReEnablesAStoppedWatcher()
    {
        string dir = Path.Combine(
            Path.GetTempPath(),
            "nm-fw-rearm-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(dir);
        try
        {
            using FileSystemWatcher watcher = new(dir) { EnableRaisingEvents = true };
            watcher.EnableRaisingEvents = false;

            bool result = FolderWatcher.TryReArm(watcher);

            Assert.True(result);
            Assert.True(watcher.EnableRaisingEvents);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryReArm_NullWatcher_ReturnsFalse()
    {
        Assert.False(FolderWatcher.TryReArm(null));
    }
}
