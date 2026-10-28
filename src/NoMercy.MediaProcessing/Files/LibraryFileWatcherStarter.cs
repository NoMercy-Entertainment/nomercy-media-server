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

using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Files;

public sealed class LibraryFileWatcherStarter(
    IStorageDriver storageDriver,
    IStorageFactory storageFactory
) : ILibraryFileWatcherStarter
{
    public void Start()
    {
        // The constructor binds the static watcher state and watches every library folder.
        _ = new LibraryFileWatcher(storageDriver, storageFactory);
    }
}
