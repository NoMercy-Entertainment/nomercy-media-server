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

namespace NoMercy.Data.Plugins;

/// <summary>
/// Judges a run that splits with RoFormer by the filter's own progress: its
/// per-chunk log lines.
/// <para>
/// No fixed time holds such a run. Loading the models takes up to a minute,
/// and a four-minute song takes about 25 minutes on a GTX 1060 and about four
/// on an RTX 3070. ffmpeg's own progress report does not help either: RoFormer
/// hands its audio on a whole chunk at a time, so the reported position stands
/// still through the load and then jumps once per chunk. The filter prints one
/// line per chunk it finishes (at ffmpeg's default log level, info), and the
/// slowest chunk measured on a GPU took just under a minute.
/// </para>
/// <para>
/// So until the filter has handed out its last chunk, the run is ended only
/// when <see cref="NoChunkLimit" /> passes without a chunk line, counted from
/// the start for the first one. After the last chunk the rest of the run is
/// the caller's to judge again: <see cref="SplitDone" /> fires once, and the
/// caller starts the limits it would have given the whole run.
/// </para>
/// <para>
/// A graph may run several RoFormer filters, one after another or side by
/// side. ffmpeg names each instance in front of its lines
/// (<c>[Parsed_stemsplit_1 @ …]</c>), so the split is only done once
/// <paramref name="filters" /> different instances have each printed their
/// last chunk. A last chunk without that name counts as one more filter done.
/// </para>
/// </summary>
/// <param name="filters">How many stemsplit filters of the graph run RoFormer; at least one.</param>
internal sealed class StemsplitProgressWatch(
    TimeSpan noChunkLimit,
    TimeProvider clock,
    int filters = 1
) : IDisposable
{
    /// <summary>How long a RoFormer run may go without a chunk line: five minutes.</summary>
    internal static readonly TimeSpan DefaultNoChunkLimit = TimeSpan.FromMinutes(5);

    // Built on the clock, so CancelAfter counts that clock's time.
    private readonly CancellationTokenSource _stalled = new(Timeout.InfiniteTimeSpan, clock);
    private readonly CancellationTokenSource _splitDone = new(Timeout.InfiniteTimeSpan, clock);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _finished = [];
    private bool _disposed;

    public TimeSpan NoChunkLimit => noChunkLimit;

    /// <summary>Cancelled once no chunk line came for <see cref="NoChunkLimit" />.</summary>
    public CancellationToken Stalled => _stalled.Token;

    /// <summary>Cancelled once the filter handed out its last chunk.</summary>
    public CancellationToken SplitDone => _splitDone.Token;

    /// <summary>Whether the missing chunk lines are what ended the run.</summary>
    public bool HasStalled => _stalled.IsCancellationRequested;

    /// <summary>Whether the filter has handed out its last chunk.</summary>
    public bool IsSplitDone => _splitDone.IsCancellationRequested;

    /// <summary>Starts the clock with the run, so a load that never ends is a stall too.</summary>
    public void Start() => Arm(noChunkLimit);

    /// <summary>One stderr line of the run. A chunk line restarts the clock; the last one stops it.</summary>
    public void Observe(string line)
    {
        StemsplitChunk? chunk = StemsplitLog.Chunk(line);
        if (chunk is null)
            return;

        bool allDone;
        lock (_gate)
        {
            if (chunk.IsLast)
                _finished.Add(chunk.Filter ?? $"unnamed filter {_finished.Count}");

            allDone = _finished.Count >= Math.Max(1, filters);
        }

        if (!allDone)
        {
            Arm(noChunkLimit);
            return;
        }

        Arm(Timeout.InfiniteTimeSpan);

        lock (_gate)
        {
            if (!_disposed)
                _splitDone.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stalled.Dispose();
            _splitDone.Dispose();
        }
    }

    /// <summary>
    /// Under a lock with <see cref="Dispose" />: the last lines of a process
    /// that was just ended can still arrive on the reader's thread after the
    /// run has returned.
    /// </summary>
    private void Arm(TimeSpan limit)
    {
        lock (_gate)
        {
            if (!_disposed && !_stalled.IsCancellationRequested)
                _stalled.CancelAfter(limit);
        }
    }
}
