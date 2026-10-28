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
using NoMercy.Storage;

namespace NoMercy.MediaProcessing.Music;

/// <summary>
/// Lists a music library tree and remembers it in memory. A folder whose own last-write
/// time is unchanged since the last listing is not enumerated again: its files and
/// subfolders are taken from memory. Only directory times are read for those folders.
/// A forced listing (manual refresh or rescan) always enumerates: a file edited in place
/// changes its own size and time, not its folder's.
/// </summary>
public sealed class MusicListingCache
{
    public static MusicListingCache Shared { get; } = new();

    private sealed record DirectorySnapshot(
        DateTime LastWriteUtc,
        List<TrackFileEntry> Files,
        List<string> SubDirectories
    );

    private readonly ConcurrentDictionary<string, DirectorySnapshot> _directories = new();
    private readonly ConcurrentDictionary<string, List<TrackFileEntry>> _lastListings = new();

    public (IReadOnlyList<TrackFileEntry> Previous, IReadOnlyList<TrackFileEntry> Current) List(
        IStorageDriver driver,
        string root,
        bool force = false
    )
    {
        string normalizedRoot = Normalize(root);
        List<TrackFileEntry> current = [];
        HashSet<string> visited = [];
        Stack<string> pending = new([normalizedRoot]);

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            visited.Add(directory);

            DirectorySnapshot snapshot = SnapshotOf(driver, directory, force);
            current.AddRange(snapshot.Files);
            foreach (string subDirectory in snapshot.SubDirectories)
                pending.Push(subDirectory);
        }

        string prefix = normalizedRoot.TrimEnd('/') + "/";
        foreach (string key in _directories.Keys)
            if (
                (key == normalizedRoot || key.StartsWith(prefix, StringComparison.Ordinal))
                && !visited.Contains(key)
            )
                _directories.TryRemove(key, out _);

        _lastListings.TryGetValue(normalizedRoot, out List<TrackFileEntry>? previous);
        _lastListings[normalizedRoot] = current;
        return (previous ?? [], current);
    }

    private DirectorySnapshot SnapshotOf(IStorageDriver driver, string directory, bool force)
    {
        DateTime lastWrite = driver.GetLastWriteTimeUtc(directory);
        if (
            !force
            && _directories.TryGetValue(directory, out DirectorySnapshot? known)
            && known.LastWriteUtc == lastWrite
        )
            return known;

        List<TrackFileEntry> files = [];
        List<string> subDirectories = [];
        foreach (
            StorageEntryInfo entry in driver.EnumerateEntries(
                directory,
                "*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            string name = Path.GetFileName(entry.Path.Replace('\\', '/').TrimEnd('/'));
            if (entry.IsDirectory)
                subDirectories.Add(directory.TrimEnd('/') + "/" + name);
            else
                files.Add(
                    new(
                        directory,
                        "/" + name,
                        entry.Size,
                        new DateTimeOffset(
                            DateTime.SpecifyKind(entry.LastWriteUtc, DateTimeKind.Utc)
                        )
                    )
                );
        }

        DirectorySnapshot snapshot = new(lastWrite, files, subDirectories);
        _directories[directory] = snapshot;
        return snapshot;
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
