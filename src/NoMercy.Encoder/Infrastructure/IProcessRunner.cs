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

namespace NoMercy.Encoder.Infrastructure;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    );

    Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs a process with a kill signal. When <paramref name="killSignal"/> fires,
    /// the process is terminated and the result is returned normally (not as an error).
    /// Use this for long-running processes like FFmpeg that may hang after output is complete.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        string? workingDirectory,
        CancellationToken cancellationToken,
        CancellationToken killSignal,
        Action<int>? onProcessStarted = null
    );

    /// <summary>
    /// Runs a process with additional environment variables merged into the
    /// child process environment. Used by disc-ripping to forward
    /// <c>LIBAACS_KEY_DB</c> / <c>LIBBDPLUS_DATABASE</c> overrides without
    /// touching the host process environment.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        IReadOnlyDictionary<string, string>? extraEnv,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs a process under a wall-clock <paramref name="timeout"/>. When the
    /// clock runs out before the process exits, the whole process tree is
    /// killed and a <see cref="TimeoutException"/> is thrown. Use this for
    /// probes and short tools (ffprobe, capability checks) that must never
    /// hang the caller. Callers that pass no timeout keep today's behaviour.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        string[] arguments,
        TimeSpan timeout,
        Action<string>? onStdOut = null,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<int>? onProcessStarted = null
    );
}
