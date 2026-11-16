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
/// Every line goes through one lock: stdout and stderr arrive on two reader
/// threads, and a line handed on directly must never overtake one still held.
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
    private bool _holding = holdBack;

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
            foreach ((bool stdErr, string line) in _held)
                Forward(stdErr, line);
            _held.Clear();
        }
    }

    private void Pass(bool stdErr, string line)
    {
        bool full;
        lock (_gate)
        {
            if (!_holding)
            {
                Forward(stdErr, line);
                return;
            }

            _held.Add((stdErr, line));
            full = _held.Count >= MostHeldLines;
        }

        if (full)
            Release();
    }

    private void Forward(bool stdErr, string line)
    {
        if (stdErr)
            onStdErr?.Invoke(line);
        else
            onStdOut?.Invoke(line);
    }
}
