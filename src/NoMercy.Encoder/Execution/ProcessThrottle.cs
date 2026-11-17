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

using Microsoft.Extensions.Logging;

namespace NoMercy.Encoder.Execution;

public class ProcessThrottle(ILogger<ProcessThrottle> logger, IProcessSuspender suspender)
{
    // Two independent holds, the operator's pause and the live-playback yield:
    // the process runs only when neither holds it. A DI singleton called from
    // every worker thread, so both sets share one lock.
    private readonly HashSet<int> _pausedPids = [];
    private readonly HashSet<int> _yieldedPids = [];
    private readonly Lock _lock = new();

    /// <summary>Operator pause: holds the process until <see cref="Resume"/>.</summary>
    public void Suspend(int processId)
    {
        // Lock spans the OS call so a concurrent Resume on the same pid can't
        // interleave between set-add and the actual SIGSTOP.
        lock (_lock)
        {
            if (!_pausedPids.Add(processId))
                return;

            if (_yieldedPids.Contains(processId))
                return;

            suspender.Suspend(processId);

            logger.LogDebug("Suspended process {Pid}", processId);
        }
    }

    /// <summary>Ends the operator pause. The process stays stopped while playback holds it.</summary>
    public void Resume(int processId)
    {
        lock (_lock)
        {
            if (!_pausedPids.Remove(processId))
                return;

            if (_yieldedPids.Contains(processId))
                return;

            suspender.Resume(processId);

            logger.LogDebug("Resumed process {Pid}", processId);
        }
    }

    /// <summary>Live playback needs the capacity: stop the background encode.</summary>
    public void YieldToPlayback(int processId)
    {
        lock (_lock)
        {
            if (!_yieldedPids.Add(processId))
                return;

            if (_pausedPids.Contains(processId))
                return;

            suspender.Suspend(processId);

            logger.LogDebug("Yielded process {Pid} to live playback", processId);
        }
    }

    /// <summary>Playback no longer needs the capacity. An operator pause still holds the process.</summary>
    public void EndYield(int processId)
    {
        lock (_lock)
        {
            if (!_yieldedPids.Remove(processId))
                return;

            if (_pausedPids.Contains(processId))
                return;

            suspender.Resume(processId);

            logger.LogDebug("Process {Pid} no longer yields to live playback", processId);
        }
    }

    /// <summary>The process exited: drop every hold so a reused pid starts clean.</summary>
    public void Forget(int processId)
    {
        lock (_lock)
        {
            _pausedPids.Remove(processId);
            _yieldedPids.Remove(processId);
        }
    }

    /// <summary>Pauses every registered encode ffmpeg; warns with <paramref name="runningEncodes"/> when none is reachable.</summary>
    public IReadOnlyList<int> SuspendEncodes(
        IEncoderProcessRegistry registry,
        IReadOnlyCollection<string> runningEncodes
    )
    {
        List<int> reached = [];
        foreach (int jobId in registry.ActiveJobIds)
        foreach (int processId in registry.GetProcessIds(jobId))
        {
            Suspend(processId);
            reached.Add(processId);
        }

        if (reached.Count == 0 && runningEncodes.Count > 0)
            logger.LogWarning(
                "Encoder pause reached no ffmpeg process; still running: {RunningEncodes}",
                string.Join(", ", runningEncodes)
            );
        else
            logger.LogInformation(
                "Encoder pause suspended {Count} ffmpeg process(es): {ProcessIds}",
                reached.Count,
                string.Join(", ", reached)
            );

        return reached;
    }

    /// <summary>Ends the operator pause of every encode ffmpeg in <paramref name="registry"/>.</summary>
    public void ResumeEncodes(IEncoderProcessRegistry registry)
    {
        foreach (int jobId in registry.ActiveJobIds)
        foreach (int processId in registry.GetProcessIds(jobId))
            Resume(processId);
    }

    public bool IsSuspended(int processId)
    {
        lock (_lock)
        {
            return _pausedPids.Contains(processId) || _yieldedPids.Contains(processId);
        }
    }
}
