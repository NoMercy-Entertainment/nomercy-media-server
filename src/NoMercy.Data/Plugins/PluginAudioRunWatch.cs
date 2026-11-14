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

using System.Globalization;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The two clocks one render runs against: its total, and how long it may go
/// without progress.
/// <para>
/// ffmpeg's <c>-progress</c> report arrives on a timer, every five seconds,
/// whether or not a single sample went through - a run reading from a mount
/// that stopped answering keeps printing the same position for ever. So a
/// report counts as progress only when it says more than the last one: a
/// later <c>out_time_us</c>, or a larger <c>total_size</c>. Anything else on
/// stdout, the graph's own output included, is not looked at.
/// </para>
/// <para>
/// Both clocks stop at ffmpeg's closing <c>progress=end</c>. Its files are
/// complete by then, and a process that hangs on its way out afterwards is
/// not a run that ran out of time.
/// </para>
/// </summary>
internal sealed class PluginAudioRunWatch(PluginAudioRunLimits limits, TimeProvider clock)
    : IDisposable
{
    private const string PositionKey = "out_time_us=";
    private const string SizeKey = "total_size=";
    private const string ReportClosed = "progress=end";

    // Built on the clock, so CancelAfter counts that clock's time.
    private readonly CancellationTokenSource _stalled = new(Timeout.InfiniteTimeSpan, clock);
    private readonly CancellationTokenSource _timedOut = new(Timeout.InfiniteTimeSpan, clock);
    private readonly Lock _gate = new();

    private long _position = long.MinValue;
    private long _size = long.MinValue;
    private bool _disposed;

    /// <summary>Cancelled once the run has made no progress for the stall limit.</summary>
    public CancellationToken Stalled => _stalled.Token;

    /// <summary>Cancelled once the run has taken its total.</summary>
    public CancellationToken TimedOut => _timedOut.Token;

    /// <summary>Whether the stall limit is what ended the run.</summary>
    public bool HasStalled => _stalled.IsCancellationRequested;

    /// <summary>
    /// Starts both clocks. The stall clock too, from the start of the run and
    /// not from its first report: a run that never gets as far as reporting
    /// is stalled as well.
    /// </summary>
    public void Start()
    {
        Arm(_timedOut, limits.Total);
        Arm(_stalled, limits.Stall);
    }

    /// <summary>One stdout line of the run. Restarts the stall clock when it shows the run moved on.</summary>
    public void Observe(string line)
    {
        if (line == ReportClosed)
        {
            Arm(_timedOut, Timeout.InfiniteTimeSpan);
            Arm(_stalled, Timeout.InfiniteTimeSpan);
            return;
        }

        if (limits.Stall is null)
            return;

        bool moved =
            Advanced(line, PositionKey, ref _position) || Advanced(line, SizeKey, ref _size);

        if (moved)
            Arm(_stalled, limits.Stall);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _stalled.Dispose();
            _timedOut.Dispose();
        }
    }

    /// <summary>
    /// Sets a clock, or stops it when <paramref name="limit" /> is infinite.
    /// Under a lock with <see cref="Dispose" />: the last lines of a process
    /// that was just ended can still arrive on the reader's thread after the
    /// run has returned, and a timer set on a disposed source throws there,
    /// where nothing catches it.
    /// </summary>
    private void Arm(CancellationTokenSource clock, TimeSpan? limit)
    {
        if (limit is not { } time)
            return;

        lock (_gate)
        {
            if (!_disposed)
                clock.CancelAfter(time);
        }
    }

    private static bool Advanced(string line, string key, ref long last)
    {
        if (
            !line.StartsWith(key, StringComparison.Ordinal)
            || !long.TryParse(
                line.AsSpan(key.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long value
            )
            || value <= last
        )
            return false;

        last = value;
        return true;
    }
}
