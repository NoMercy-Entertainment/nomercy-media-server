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

using Microsoft.Extensions.Logging;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Libraries;
using NoMercy.MediaProcessing.Music;
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.EventHandlers;

/// <summary>
/// Keeps a share that went offline from turning into deleted rows. A delete event is only
/// believed while the library folder root answers with entries, and while deletes do not
/// arrive in a burst that would empty the library.
/// </summary>
internal sealed class FileDeleteGuard(IStorageFactory storageFactory, ILogger logger)
{
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly Dictionary<Ulid, Queue<DateTime>> _recentDeletes = [];

    public bool RootIsOnline(Folder folder)
    {
        try
        {
            IStorage storage = storageFactory.For(folder.Id, folder.DriverId, string.Empty);
            string root = LibraryManager.ResolveScanRoot(storage, folder.Path);
            if (
                storage.Driver.DirectoryExists(root)
                && storage
                    .Driver.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly)
                    .Any()
            )
                return true;

            logger.LogWarning(
                "FileWatcher: ignoring the delete, library folder root '{Root}' is unreadable or empty",
                root
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "FileWatcher: ignoring the delete, library folder '{Path}' failed to list: {Message}",
                [folder.Path, ex.Message]
            );
        }

        return false;
    }

    public bool IsBurst(Ulid libraryId, int knownFiles, DateTime nowUtc)
    {
        int recent;
        lock (_lock)
        {
            if (!_recentDeletes.TryGetValue(libraryId, out Queue<DateTime>? deletes))
                _recentDeletes[libraryId] = deletes = new();

            deletes.Enqueue(nowUtc);
            while (deletes.Count > 0 && nowUtc - deletes.Peek() > BurstWindow)
                deletes.Dequeue();
            recent = deletes.Count;
        }

        if (!MissingFileGuard.ShouldSkipDeletes(knownFiles, recent))
            return false;

        logger.LogWarning(
            "FileWatcher: ignoring the delete, {Recent} of {Known} files went missing within a minute",
            [recent, knownFiles]
        );
        return true;
    }
}
