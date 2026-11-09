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
using System.Text;
using NoMercy.Storage;

namespace NoMercy.Api.Middleware;

/// <summary>
/// Bounded in-memory copy of the small files a play opens first (playlists and
/// vtt tracks), so a repeat read never pays the storage open. An entry is valid
/// only while the file's size and last-write time match what was cached, so a
/// re-encode that rewrites a playlist is a miss and the new bytes replace the old.
/// </summary>
public sealed class PlaybackFileCache
{
    internal const long MaxFileBytes = 512 * 1024;
    internal const long MaxTotalBytes = 32 * 1024 * 1024;
    internal const int MaxEntries = 1024;
    private const int MaxWarmedPlaylists = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<(Ulid Folder, string Path), Entry> _entries = [];
    private readonly ConcurrentDictionary<(Ulid Folder, string Path), byte> _warming = new();
    private long _totalBytes;
    private long _clock;

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public static bool IsCacheable(string relativePath) =>
        relativePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        || relativePath.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file's bytes, from memory when size and last-write time still match,
    /// otherwise read once and remembered. Null when the file is empty or too big
    /// to cache; the caller then serves it from storage as before.
    /// </summary>
    public byte[]? Load(IStorage storage, Ulid folderId, string relativePath)
    {
        long size = storage.Size(relativePath);
        if (size <= 0 || size > MaxFileBytes)
            return null;

        DateTimeOffset modified = storage.LastModified(relativePath);
        (Ulid, string) key = (folderId, relativePath);

        lock (_gate)
        {
            if (
                _entries.TryGetValue(key, out Entry? hit)
                && hit.Size == size
                && hit.Modified == modified
            )
            {
                hit.LastUsed = ++_clock;
                return hit.Bytes;
            }
        }

        byte[] bytes;
        using (Stream stream = storage.OpenRead(relativePath))
        {
            using MemoryStream buffer = new((int)size);
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        // The file changed between the stat and the read: serve it, but do not trust the key.
        if (bytes.Length != size)
            return bytes;

        Store(key, new Entry(bytes, size, modified));
        return bytes;
    }

    /// <summary>
    /// Loads the playlists a master names into the cache in the background.
    /// The returned task never faults; callers may drop it.
    /// </summary>
    public Task WarmReferencedPlaylistsAsync(
        IStorage storage,
        Ulid folderId,
        string masterPath,
        byte[] masterBytes
    )
    {
        List<string> targets = ReferencedPlaylists(
            masterPath,
            Encoding.UTF8.GetString(masterBytes)
        );
        if (targets.Count == 0)
            return Task.CompletedTask;

        return Task.WhenAll(
            targets.Select(target =>
                Task.Run(() =>
                {
                    (Ulid, string) key = (folderId, target);
                    if (!_warming.TryAdd(key, 0))
                        return;
                    try
                    {
                        Load(storage, folderId, target);
                    }
                    catch (Exception)
                    {
                        // Best effort: the real request reports a missing or broken file.
                    }
                    finally
                    {
                        _warming.TryRemove(key, out _);
                    }
                })
            )
        );
    }

    internal static List<string> ReferencedPlaylists(string masterPath, string masterText)
    {
        int lastSlash = masterPath.LastIndexOf('/');
        string directory = lastSlash < 0 ? string.Empty : masterPath[..(lastSlash + 1)];
        List<string> found = [];

        foreach (string rawLine in masterText.Split('\n'))
        {
            string line = rawLine.Trim();
            string? reference = line.StartsWith('#') ? AttributeUri(line) : line;
            if (string.IsNullOrEmpty(reference))
                continue;

            int query = reference.IndexOf('?');
            if (query >= 0)
                reference = reference[..query];

            if (!IsSafePlaylistReference(reference))
                continue;

            string target = directory + reference;
            if (!found.Contains(target, StringComparer.Ordinal))
                found.Add(target);
            if (found.Count == MaxWarmedPlaylists)
                break;
        }

        return found;
    }

    private static string? AttributeUri(string tagLine)
    {
        const string marker = "URI=\"";
        int start = tagLine.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        int end = tagLine.IndexOf('"', start);
        return end < 0 ? null : tagLine[start..end];
    }

    private static bool IsSafePlaylistReference(string reference) =>
        reference.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
        && !reference.StartsWith('/')
        && !reference.Contains("://", StringComparison.Ordinal)
        && !reference.Contains("..", StringComparison.Ordinal)
        && !reference.Contains('\\');

    private void Store((Ulid, string) key, Entry entry)
    {
        lock (_gate)
        {
            if (_entries.Remove(key, out Entry? old))
                _totalBytes -= old.Bytes.Length;

            while (
                _entries.Count > 0
                && (
                    _entries.Count >= MaxEntries || _totalBytes + entry.Bytes.Length > MaxTotalBytes
                )
            )
            {
                (Ulid, string) oldest = _entries.MinBy(pair => pair.Value.LastUsed).Key;
                _totalBytes -= _entries[oldest].Bytes.Length;
                _entries.Remove(oldest);
            }

            entry.LastUsed = ++_clock;
            _entries[key] = entry;
            _totalBytes += entry.Bytes.Length;
        }
    }

    private sealed class Entry(byte[] bytes, long size, DateTimeOffset modified)
    {
        public byte[] Bytes { get; } = bytes;
        public long Size { get; } = size;
        public DateTimeOffset Modified { get; } = modified;
        public long LastUsed { get; set; }
    }
}
