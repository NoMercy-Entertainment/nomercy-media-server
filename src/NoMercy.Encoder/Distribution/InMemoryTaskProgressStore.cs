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

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// Thread-safe in-memory store. A hard cap of <see cref="MaxEntries"/> drops the
/// oldest task first, and <see cref="StaleAfter"/> hides old snapshots from
/// <see cref="GetAll"/>.
/// </summary>
public class InMemoryTaskProgressStore : ITaskProgressStore
{
    private const int MaxEntries = 500;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, TaskProgressSnapshot> _snapshots = new();
    private readonly ConcurrentQueue<string> _order = new();

    public void Update(string taskId, TaskProgressSnapshot snapshot)
    {
        if (!_snapshots.TryAdd(taskId, snapshot))
        {
            _snapshots[taskId] = snapshot;
            return;
        }

        // Hard cap by count: drop the oldest-inserted task, no scan of the map.
        _order.Enqueue(taskId);
        while (_snapshots.Count > MaxEntries && _order.TryDequeue(out string? oldest))
            _snapshots.TryRemove(oldest, out _);
    }

    public TaskProgressSnapshot? Get(string taskId) =>
        _snapshots.TryGetValue(taskId, out TaskProgressSnapshot? snap) ? snap : null;

    public IReadOnlyList<TaskProgressSnapshot> GetAll()
    {
        DateTime cutoff = DateTime.UtcNow - StaleAfter;
        return _snapshots.Values.Where(s => s.ReceivedAtUtc >= cutoff).ToArray();
    }
}
