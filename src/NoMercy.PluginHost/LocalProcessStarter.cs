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
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginHost;

/// <summary>What the plugin process was permitted to run, after the server answered.</summary>
public sealed record LocalProcessRequest(
    string ResolvedPath,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment,
    string? WorkingDirectory
);

/// <summary>
/// Starting the child, kept behind an interface so the guards around it can be
/// tested without spawning anything: what matters is WHICH file is reached and
/// with what, and a real process proves neither.
/// </summary>
public interface ILocalProcessStarter
{
    IPluginProcessHandle Start(LocalProcessRequest request);
}

/// <summary>
/// Starts the child for real, from inside the plugin's own process.
/// <para>
/// This is what puts the child in the sandbox. A process started on the server
/// side would be a child of the server and would sit beside the job object,
/// cgroup or sandbox profile holding the plugin; started here it is a child of
/// the confined process and inherits all of it.
/// </para>
/// </summary>
public sealed class LocalProcessStarter : ILocalProcessStarter
{
    public IPluginProcessHandle Start(LocalProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo start = new()
        {
            FileName = request.ResolvedPath,
            // Never through a shell: UseShellExecute would re-interpret the
            // arguments and reintroduce the PATH search the approved-binary
            // check exists to remove.
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        // Added to the list rather than joined into a string, so a path with a
        // space in it is one argument and not two.
        foreach (string argument in request.Arguments)
            start.ArgumentList.Add(argument);

        foreach (
            (string key, string value) in request.Environment ?? new Dictionary<string, string>()
        )
            start.Environment[key] = value;

        Process process =
            Process.Start(start)
            ?? throw new InvalidOperationException(
                $"The operating system started no process for {request.ResolvedPath}."
            );

        return new LocalProcessHandle(process);
    }
}

/// <summary>
/// One child process the plugin started.
/// <para>
/// Output is handed over as readers rather than collected into strings: a
/// transcode writes progress for an hour, and buffering that is the plugin
/// holding a log nobody asked for.
/// </para>
/// </summary>
public sealed class LocalProcessHandle(Process process) : IPluginProcessHandle
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
