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

namespace NoMercy.Data.Services;

/// <summary>
/// The server used to keep stems and rendered transitions in a folder of its
/// own inside the cache. It no longer analyses music, so nothing reads that
/// folder and nothing would ever clean it up: on a server that used it, it can
/// hold gigabytes. This removes it.
/// <para>
/// A plugin that renders audio keeps it in its own folder, which this does not
/// touch.
/// </para>
/// <para>
/// Safe to run on every start: once the folder is gone there is nothing to do,
/// and the server no longer creates it.
/// </para>
/// </summary>
/// <param name="cacheStorage">A storage whose root is the server's cache folder.</param>
public class RetiredDerivedAudioFolder(IStorage cacheStorage) : IRetiredDerivedAudioFolder
{
    /// <summary>The folder's name inside the cache folder.</summary>
    public const string FolderName = "derived";

    public async Task<long?> RemoveAsync(CancellationToken cancellationToken)
    {
        if (!await cacheStorage.ExistsAsync(FolderName, cancellationToken))
            return null;

        long bytes = 0;
        await foreach (
            StorageEntry entry in cacheStorage.ListAsync(
                FolderName,
                pattern: null,
                recursive: true,
                cancellationToken
            )
        )
        {
            if (!entry.IsDirectory)
                bytes += entry.SizeBytes;
        }

        await cacheStorage.DeleteDirectoryAsync(FolderName, recursive: true, cancellationToken);

        return bytes;
    }
}
