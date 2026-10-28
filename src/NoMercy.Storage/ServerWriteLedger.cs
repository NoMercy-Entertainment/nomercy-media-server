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

namespace NoMercy.Storage;

/// <summary>
/// Short-lived, in-memory record of the paths this server just wrote or moved, and of the
/// folders it is writing into. The library file watcher skips those paths, so the server's
/// own output (encoder publish, organize moves) never queues an import again.
/// </summary>
public sealed class ServerWriteLedger(TimeProvider? clock = null)
{
    public static ServerWriteLedger Shared { get; } = new();

    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(30);

    private const int PruneAbove = 4096;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _paths = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, DateTimeOffset> _folders = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly ConcurrentDictionary<string, int> _held = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The server wrote or moved this file or folder just now.</summary>
    public void Note(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _paths[Normalize(path)] = _clock.GetUtcNow() + Linger;
        if (_paths.Count > PruneAbove)
            Prune();
    }

    /// <summary>The server moved this folder just now; the folder and everything under it count.</summary>
    public void NoteFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        _folders[Normalize(folder)] = _clock.GetUtcNow() + Linger;
        if (_folders.Count > PruneAbove)
            Prune();
    }

    /// <summary>
    /// Everything under this folder counts as server output until the returned handle is
    /// disposed, and for <see cref="Linger"/> after that, while the file events arrive.
    /// </summary>
    public IDisposable Hold(string folder)
    {
        string key = Normalize(folder);
        _held.AddOrUpdate(key, 1, (_, count) => count + 1);
        return new Release(this, key);
    }

    public bool IsServerWritten(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        DateTimeOffset now = _clock.GetUtcNow();
        string normalized = Normalize(path);
        if (_paths.TryGetValue(normalized, out DateTimeOffset until) && until > now)
            return true;

        return _held.Keys.Any(folder => Covers(folder, normalized))
            || _folders.Any(entry => entry.Value > now && Covers(entry.Key, normalized));
    }

    private static bool Covers(string folder, string path) =>
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)
        || path.Equals(folder, StringComparison.OrdinalIgnoreCase);

    private void Prune()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        foreach ((string path, DateTimeOffset until) in _paths)
            if (until <= now)
                _paths.TryRemove(path, out _);
        foreach ((string folder, DateTimeOffset until) in _folders)
            if (until <= now)
                _folders.TryRemove(folder, out _);
    }

    public static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private sealed class Release(ServerWriteLedger ledger, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
                return;
            ledger._folders[key] = ledger._clock.GetUtcNow() + Linger;
            if (ledger._held.AddOrUpdate(key, 0, (_, count) => count - 1) <= 0)
                ledger._held.TryRemove(key, out _);
            ledger.Prune();
        }
    }
}
