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
using NoMercy.Storage;

namespace NoMercy.Queue.MediaServer.Jobs;

public class ImageCacheSweeper(IStorage storage, ILogger<ImageCacheSweeper> logger)
{
    private const string OriginalFolder = "original";
    private const string MusicFolder = "music";

    private sealed record CacheFile(
        string Path,
        string Folder,
        string Name,
        long Bytes,
        DateTimeOffset LastUsed
    );

    /// <summary>
    /// Removes originals no item refers to once they have been unused for the
    /// grace period, then evicts the least recently used files until the cache
    /// is under <paramref name="maxBytes"/> (0 = no limit). Only files go. The
    /// music folder stays: its covers cannot always be fetched again.
    /// </summary>
    public ImageCacheSweepResult Sweep(
        string root,
        IReadOnlySet<string> referencedNames,
        long maxBytes,
        TimeSpan orphanGrace,
        DateTimeOffset now
    )
    {
        if (!storage.Exists(root))
            return new(0, 0, 0, 0, 0);

        List<CacheFile> files = ListFiles(root);

        int orphansRemoved = 0;
        long orphanBytes = 0;
        foreach (
            CacheFile file in files
                .Where(f => IsOrphan(f, referencedNames, orphanGrace, now))
                .ToList()
        )
        {
            if (!TryDelete(file))
                continue;

            files.Remove(file);
            orphansRemoved++;
            orphanBytes += file.Bytes;
        }

        long total = files.Sum(f => f.Bytes);
        int evictedFiles = 0;
        long evictedBytes = 0;
        if (maxBytes > 0)
        {
            foreach (CacheFile file in files.OrderBy(f => f.LastUsed))
            {
                if (total <= maxBytes)
                    break;

                if (!TryDelete(file))
                    continue;

                total -= file.Bytes;
                evictedFiles++;
                evictedBytes += file.Bytes;
            }
        }

        return new(orphansRemoved, orphanBytes, evictedFiles, evictedBytes, total);
    }

    private List<CacheFile> ListFiles(string root)
    {
        // Listed paths are relative to the storage scope, not to the folder listed,
        // so the folder of a file is read from the folder entry it was listed under.
        List<CacheFile> files = [];
        foreach (StorageEntry entry in storage.List(root, null, recursive: false))
        {
            if (!entry.IsDirectory)
            {
                files.Add(ToCacheFile(entry, string.Empty));
                continue;
            }

            string folder = storage.GetName(entry.Path);
            if (folder == MusicFolder)
                continue;

            string folderPath = entry.Path.Replace('\\', '/');
            foreach (StorageEntry child in storage.List(entry.Path, null, recursive: true))
            {
                if (child.IsDirectory)
                    continue;

                bool directChild =
                    child.Path.Replace('\\', '/').LastIndexOf('/') == folderPath.Length;
                files.Add(ToCacheFile(child, directChild ? folder : string.Empty));
            }
        }

        return files;
    }

    private CacheFile ToCacheFile(StorageEntry entry, string folder) =>
        new(entry.Path, folder, storage.GetName(entry.Path), entry.SizeBytes, entry.LastModified);

    private static bool IsOrphan(
        CacheFile file,
        IReadOnlySet<string> referencedNames,
        TimeSpan orphanGrace,
        DateTimeOffset now
    )
    {
        return file.Folder == OriginalFolder
            && !referencedNames.Contains(file.Name)
            && now - file.LastUsed > orphanGrace;
    }

    private bool TryDelete(CacheFile file)
    {
        try
        {
            storage.Delete(file.Path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Image cache could not remove {Path}", file.Path);
            return false;
        }
    }
}
