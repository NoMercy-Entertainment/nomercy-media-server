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

using System.Diagnostics;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Runtime;

/// <summary>
/// One child process the plugin started.
/// <para>
/// Output is handed over as readers rather than collected into strings: a
/// transcode writes progress for an hour, and buffering that is the server
/// holding a log nobody asked for.
/// </para>
/// </summary>
public sealed class PluginProcessHandle(Process process) : IPluginProcessHandle
{
    public int Id { get; } = process.Id;

    public bool HasExited => process.HasExited;

    public int ExitCode => process.ExitCode;

    public TextReader StandardOutput => process.StandardOutput;

    public TextReader StandardError => process.StandardError;

    public async Task<int> WaitForExitAsync(CancellationToken ct = default)
    {
        await process.WaitForExitAsync(ct);

        return process.ExitCode;
    }

    public void Kill()
    {
        if (process.HasExited)
            return;

        // The whole tree: a transcode that spawned its own helper leaves the
        // helper holding the file when only the parent is killed.
        process.Kill(entireProcessTree: true);
    }

    /// <summary>
    /// Killed, not merely released. This is what the ledger disposes when the
    /// plugin stops, and a child that outlives the plugin that started it is
    /// one nobody is left to stop.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        try
        {
            Kill();
        }
        catch (Exception)
        {
            // A process that has already gone, or one this user may no longer
            // signal, is not a reason to fail the plugin's shutdown.
        }

        process.Dispose();

        return ValueTask.CompletedTask;
    }
}
