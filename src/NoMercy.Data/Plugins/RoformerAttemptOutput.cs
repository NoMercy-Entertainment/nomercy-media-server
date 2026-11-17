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
/// The plugin's output callbacks for one run the host started with RoFormer
/// in place of a source token, which the filter may still refuse.
/// <para>
/// A refused run is the host's business: it runs the graph again with
/// Spleeter, and the plugin is meant to see one run. So the lines are held
/// back until the filter shows it is running (its first chunk line) and then
/// handed on in the order they came; after a refusal they are dropped. A run
/// that ends any other way hands on what it held when the caller calls
/// <see cref="Release" />. The filter refuses before it computes anything, so
/// what is held is the banner and the model loading, a few dozen lines;
/// past <see cref="MostHeldLines" /> the lines are handed on regardless.
/// </para>
/// <para>
/// stdout and stderr arrive on two reader threads, and a line handed on
/// must never overtake one still held. So every line goes into one queue
/// under a lock, in the order it came, and one thread at a time hands the
/// queue on to the plugin, outside the lock: a slow plugin callback holds up
/// the lines behind it, never the other reader's bookkeeping.
/// </para>
/// </summary>
internal sealed class RoformerAttemptOutput(
    Action<string>? onStdOut,
    Action<string>? onStdErr,
    bool holdBack
)
{
    internal const int MostHeldLines = 10_000;

    private readonly Lock _gate = new();
    private readonly List<(bool StdErr, string Line)> _held = [];
    private readonly Queue<(bool StdErr, string Line)> _ready = new();
    private bool _holding = holdBack;
    private bool _handingOn;

    /// <summary>The filter's refusal, once it gave one.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The backend and device the filter said RoFormer runs on.</summary>
    public string? Device { get; private set; }

    public void StdOut(string line) => Pass(stdErr: false, line);

    public void StdErr(string line)
    {
        bool running;
        lock (_gate)
        {
            if (Refusal is null && StemsplitLog.IsRefusal(line))
                Refusal = line;

            Device ??= StemsplitLog.RoformerDevice(line);
            running = StemsplitLog.Chunk(line) is not null;
        }

        Pass(stdErr: true, line);

        if (running)
            Release();
    }

    /// <summary>Hands on everything held, and every line after it directly.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (!_holding)
                return;

            _holding = false;
            foreach ((bool StdErr, string Line) line in _held)
                _ready.Enqueue(line);
            _held.Clear();
        }

        HandOn();
    }

    private void Pass(bool stdErr, string line)
    {
        bool full = false;
        lock (_gate)
        {
            if (_holding)
            {
                _held.Add((stdErr, line));
                full = _held.Count >= MostHeldLines;
            }
            else
            {
                _ready.Enqueue((stdErr, line));
            }
        }

        if (full)
            Release();
        else
            HandOn();
    }

    /// <summary>
    /// Hands the queued lines on, in order, unless another thread already is:
    /// that thread takes this one's lines too, since it only stops once the
    /// queue is empty under the lock.
    /// </summary>
    private void HandOn()
    {
        lock (_gate)
        {
            if (_handingOn)
                return;

            _handingOn = true;
        }

        while (true)
        {
            (bool StdErr, string Line) next;
            lock (_gate)
            {
                if (!_ready.TryDequeue(out next))
                {
                    _handingOn = false;
                    return;
                }
            }

            try
            {
                if (next.StdErr)
                    onStdErr?.Invoke(next.Line);
                else
                    onStdOut?.Invoke(next.Line);
            }
            catch
            {
                // A callback that throws must not leave the next line waiting
                // for a hand-on that never comes.
                lock (_gate)
                    _handingOn = false;
                throw;
            }
        }
    }
}
