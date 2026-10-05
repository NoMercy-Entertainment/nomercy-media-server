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
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Encoder.Infrastructure;

/// <summary>
/// Captures stdout/stderr lines from a child process and exposes them as a
/// single string at end-of-run, while capping memory at a hard byte budget.
///
/// The previous implementation buffered everything into an unbounded
/// <see cref="StringBuilder"/>. Long ffmpeg runs (multi-rung HLS bundles,
/// two-pass encodes) emit per-frame log lines for the entire duration —
/// when nothing trims the buffer it can grow to tens of MB, and the final
/// <c>ToString()</c> + every error-classification read became O(n) over
/// useless filter-init noise. Capping at the last <see cref="MaxBytes"/>
/// keeps the tail intact for error parsing without burning CPU on chatter.
/// </summary>
internal sealed class BoundedLineBuffer(int maxBytes)
{
    private readonly Queue<string> _lines = new();
    public int MaxBytes { get; } = maxBytes;
    private int _byteCount;

    public void AppendLine(string line)
    {
        _lines.Enqueue(line);
        // +1 for the implicit '\n' separator added by string.Join below.
        _byteCount += line.Length + 1;
        while (_byteCount > MaxBytes && _lines.Count > 1)
            _byteCount -= _lines.Dequeue().Length + 1;
    }

    public override string ToString() => string.Join('\n', _lines);
}

public class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    // 256 KB stderr keeps the tail of any reasonable failure intact (banner +
    // filter-init + actual error are well under this on every real-world
    // failure mode) without retaining megabytes of per-frame logspam over a
    // multi-hour encode.
    private const int StdErrMaxBytes = 256 * 1024;

    public Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        return RunAsync(executable, arguments, null, null, workingDirectory, cancellationToken);
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        return RunCoreAsync(
            executable,
            arguments,
            onStdOut,
            onStdErr,
            workingDirectory,
            cancellationToken,
            killSignal: default
        );
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        string? workingDirectory,
        CancellationToken cancellationToken,
        CancellationToken killSignal,
        Action<int>? onProcessStarted = null
    )
    {
        return RunCoreAsync(
            executable,
            arguments,
            onStdOut,
            onStdErr,
            workingDirectory,
            cancellationToken,
            killSignal,
            onProcessStarted
        );
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        IReadOnlyDictionary<string, string>? extraEnv,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        return RunCoreAsync(
            executable,
            arguments,
            onStdOut: null,
            onStdErr: null,
            workingDirectory,
            cancellationToken,
            killSignal: default,
            onProcessStarted: null,
            extraEnv: extraEnv
        );
    }

    public Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<int>? onProcessStarted = null
    )
    {
        return RunCoreAsync(
            executable,
            arguments,
            onStdOut,
            onStdErr,
            workingDirectory,
            cancellationToken,
            killSignal: default,
            onProcessStarted,
            extraEnv: null,
            timeout
        );
    }

    private async Task<ProcessResult> RunCoreAsync(
        string executable,
        string[] arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        string? workingDirectory,
        CancellationToken cancellationToken,
        CancellationToken killSignal,
        Action<int>? onProcessStarted = null,
        IReadOnlyDictionary<string, string>? extraEnv = null,
        TimeSpan? timeout = null
    )
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        StringBuilder stdOutBuilder = new();
        BoundedLineBuffer stdErrBuilder = new(StdErrMaxBytes);

        // Working directories on Windows must exist before Process.Start —
        // otherwise the API surfaces Win32 error 2 (ERROR_FILE_NOT_FOUND)
        // with a misleading "An error occurred trying to start process ..."
        // message that points at the executable instead of the missing dir.
        // Defensive create avoids spurious failures from upstream cleanup races.
        string resolvedWorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        if (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            logger.LogWarning(
                "Working directory {Dir} missing at Process.Start; creating",
                workingDirectory
            );
            Directory.CreateDirectory(workingDirectory);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = resolvedWorkingDirectory,
            // Every child here writes UTF-8; unset, .NET falls back to the OEM
            // console codepage and mangles any multi-byte character. See commit.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (extraEnv is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> kv in extraEnv)
                startInfo.Environment[kv.Key] = kv.Value;
        }

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        logger.LogDebug(
            "Starting process: {Executable} {Arguments}",
            executable,
            string.Join(" ", arguments)
        );

        using Process process = new() { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdOutBuilder.AppendLine(e.Data);
            onStdOut?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdErrBuilder.AppendLine(e.Data);
            onStdErr?.Invoke(e.Data);
        };

        // When killSignal fires, terminate the process tree.
        // This is NOT an error — the caller decided output is complete.
        bool killedBySignal = false;
        CancellationTokenRegistration killRegistration = default;
        if (killSignal.CanBeCanceled)
        {
            killRegistration = killSignal.Register(() =>
            {
                killedBySignal = true;
                try
                {
                    if (!process.HasExited)
                    {
                        logger.LogDebug(
                            "Kill signal received — terminating process: {Executable}",
                            executable
                        );
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException) { }
            });
        }

        // Every child is tied to the server's job object, so a shutdown takes the
        // whole tree with it. Killing the process on the way out only covers the
        // paths that reach their own cleanup; a crash or a stop mid-probe left
        // ffmpeg and ffprobe running with nothing left to reap them.
        process.Start();
        Shell.ChildProcessManager.Attach(process);

        // Bind the child to the server's kill-on-close job object so a hard
        // crash/kill of the server (where the graceful cancellation path never
        // runs) can't leave an orphaned ffmpeg. No-op fallback off Windows.
        Shell.ProcessHelper.AttachToParentLifetime(process);

        onProcessStarted?.Invoke(process.Id);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wall-clock timeout: a probe that never exits (a stuck ffprobe on a
        // broken disc, a tool waiting on a pipe nobody feeds) must not hang
        // the caller forever. The timer kills the whole tree; the flag is set
        // only when the kill really happened, so a process that exits on its
        // own in the same instant is still reported as a normal exit.
        bool timedOut = false;
        using CancellationTokenSource? timeoutCts = timeout is { } limit ? new(limit) : null;
        CancellationTokenRegistration timeoutRegistration = default;
        if (timeoutCts is not null)
        {
            timeoutRegistration = timeoutCts.Token.Register(() =>
            {
                try
                {
                    if (process.HasExited)
                        return;
                    logger.LogWarning(
                        "Process exceeded its {Timeout} wall-clock timeout — terminating process tree: {Executable}",
                        timeout,
                        executable
                    );
                    process.Kill(entireProcessTree: true);
                    timedOut = true;
                }
                catch (InvalidOperationException) { }
            });
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (killedBySignal && !cancellationToken.IsCancellationRequested)
        {
            // Process was killed by our kill signal, not by user cancellation.
            // Wait briefly for the kill to finalize.
            try
            {
                using CancellationTokenSource finalizeCts = new(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(finalizeCts.Token);
            }
            catch (Exception)
            {
                // Best effort — process is already being killed; if the
                // finalize wait fails or times out, exit code is reported
                // as the kill-signal value either way.
            }
        }
        catch (OperationCanceledException)
        {
            // User-initiated cancellation: attempt a graceful shutdown first,
            // then force-kill if FFmpeg does not exit within the grace period.
            await KillGracefullyAsync(process, executable, logger);
            throw;
        }
        finally
        {
            await killRegistration.DisposeAsync();
            await timeoutRegistration.DisposeAsync();
        }

        // WaitForExitAsync returns when the process exits but does NOT wait
        // for the redirected output/error streams to drain or for the OS to
        // finalize the exit code — that's a known .NET race documented at
        // https://learn.microsoft.com/dotnet/api/system.diagnostics.process.waitforexit.
        // The sync WaitForExit() with no timeout completes those steps;
        // skipping it is what produced spurious exit -1 readings on long
        // ffmpeg runs (rip cut at 32:24 of 40:23 with no actual error).
        process.WaitForExit();

        stopwatch.Stop();

        if (timedOut)
        {
            throw new TimeoutException(
                $"Process '{executable}' exceeded its wall-clock timeout of {timeout} "
                    + $"after {stopwatch.Elapsed.TotalSeconds:F1}s and was killed with its process tree."
            );
        }

        int exitCode = killedBySignal ? 0 : process.ExitCode;

        ProcessResult result = new(
            ExitCode: exitCode,
            StdOut: stdOutBuilder.ToString().TrimEnd(),
            StdErr: stdErrBuilder.ToString().TrimEnd(),
            Duration: stopwatch.Elapsed,
            ProcessId: process.Id
        );

        logger.LogDebug(
            "Process exited: {Executable} ExitCode={ExitCode} Duration={Duration}ms{KillNote}",
            executable,
            result.ExitCode,
            result.Duration.TotalMilliseconds,
            killedBySignal ? " (killed by signal)" : ""
        );

        return result;
    }

    /// <summary>
    /// Attempts a graceful shutdown before resorting to force-kill.
    /// On Windows: <c>CloseMainWindow()</c> sends WM_CLOSE (analogous to
    /// Ctrl+C for console apps); if the process does not exit within 5 s,
    /// falls back to <c>Kill(entireProcessTree: true)</c>.
    /// On Linux/macOS: <c>Kill(false)</c> sends SIGTERM; same 5 s grace,
    /// then <c>Kill(true)</c> (SIGKILL).
    /// </summary>
    private static async Task KillGracefullyAsync(
        Process process,
        string executable,
        ILogger logger
    )
    {
        if (process.HasExited)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // CloseMainWindow sends WM_CLOSE to the console window;
                // FFmpeg treats this like Ctrl+C and flushes output before exiting.
                process.CloseMainWindow();
            }
            else
            {
                // SIGTERM — FFmpeg catches this and exits cleanly.
                process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
            return; // already exited
        }

        // Wait up to 5 s for graceful exit.
        try
        {
            using CancellationTokenSource graceCts = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(graceCts.Token);
            logger.LogDebug(
                "Process exited gracefully after cancel signal: {Executable}",
                executable
            );
            return;
        }
        catch (Exception)
        {
            // Grace period expired or wait failed — fall through to force-kill.
        }

        // Force-kill the entire process tree.
        try
        {
            if (!process.HasExited)
            {
                logger.LogDebug(
                    "Grace period expired — force-killing process tree: {Executable}",
                    executable
                );
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
    }
}
