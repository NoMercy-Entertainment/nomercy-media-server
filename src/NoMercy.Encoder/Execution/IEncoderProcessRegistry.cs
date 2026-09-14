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

namespace NoMercy.Encoder.Execution;

/// <summary>
/// Tracks live FFmpeg process IDs per encode job so the dashboard (or any
/// out-of-band caller) can locate them for pause / resume / cancel operations.
/// Keyed by the public job id that the dashboard uses — for video encodes this
/// is the movie / episode id.
/// </summary>
public interface IEncoderProcessRegistry
{
    /// <summary>
    /// Record that <paramref name="processId"/> is running for <paramref name="jobId"/>.
    /// Idempotent — registering the same pid twice is a no-op.
    /// </summary>
    void Register(int jobId, int processId);

    /// <summary>
    /// Record that <paramref name="processId"/> is running for <paramref name="jobId"/>
    /// with the given ffmpeg argument list. The argv is used by
    /// <see cref="CountConcurrentNvencSessions"/> to determine whether the process
    /// is using an NVENC codec variant.
    /// </summary>
    void RegisterWithArgv(int jobId, int processId, string[] argv);

    /// <summary>
    /// Remove a single process mapping. Call when the process exits.
    /// </summary>
    void Unregister(int jobId, int processId);

    /// <summary>
    /// Remove all processes for a job. Call when the job completes or fails.
    /// </summary>
    void UnregisterJob(int jobId);

    /// <summary>
    /// Returns the process ids currently running for a job, or an empty set
    /// when the job is not running.
    /// </summary>
    IReadOnlyCollection<int> GetProcessIds(int jobId);

    /// <summary>
    /// All registered job ids currently known to the registry.
    /// </summary>
    IReadOnlyCollection<int> ActiveJobIds { get; }

    /// <summary>
    /// Returns the number of currently registered ffmpeg processes whose argv
    /// contains an NVENC codec flag (<c>h264_nvenc</c>, <c>hevc_nvenc</c>, or
    /// <c>av1_nvenc</c>). Only processes registered via
    /// <see cref="RegisterWithArgv"/> are counted — processes registered via
    /// the argv-less <see cref="Register"/> overload are not counted.
    /// </summary>
    int CountConcurrentNvencSessions();

    /// <summary>
    /// Kills every ffmpeg process registered for <paramref name="jobId"/> (whole
    /// process tree) and unregisters each one, so a caller that removes a queue
    /// row can stop the live encode behind it without reaching into
    /// <see cref="System.Diagnostics.Process"/> itself. A process that has
    /// already exited, or whose pid is stale, is treated as already stopped.
    /// </summary>
    void KillProcesses(int jobId);
}
