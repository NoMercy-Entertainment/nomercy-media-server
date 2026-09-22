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
using Microsoft.Extensions.Logging.Abstractions;

namespace NoMercy.NmSystem.Lifecycle;

public sealed class ServerPhaseTracker : IServerPhaseTracker
{
    // Static accessor so static boot code (Setup.Start, Setup.Binaries) can advance
    // stages without threading DI through every helper. Wired by ServiceConfiguration
    // immediately after the DI container is built.
    public static IServerPhaseTracker? Current { get; private set; }

    public static void RegisterCurrent(IServerPhaseTracker tracker) => Current = tracker;

    // Process-wide singleton. The Service rebuilds its WebApplication on the HTTPS
    // restart (and again on port-conflict retry), giving each host its own DI
    // container. A per-container tracker meant the live host's queue workers gated
    // on a tracker the static MarkComplete callers (BootOrchestrator, Setup.Start)
    // never reached — so jobs sat in the queue forever. Returning the same instance
    // from every container keeps the gate aligned with the markers.
    private static readonly object SharedLock = new();
    private static ServerPhaseTracker? _shared;

    public static ServerPhaseTracker Shared(ILogger<ServerPhaseTracker>? logger = null)
    {
        lock (SharedLock)
        {
            if (_shared is null)
            {
                _shared = new(logger);
            }
            else if (logger is not null)
            {
                _shared._logger = logger;
            }

            Current = _shared;
            return _shared;
        }
    }

    // Test seam — process-wide singleton would otherwise leak state across xUnit
    // collections that share the AppDomain.
    internal static void ResetSharedForTests()
    {
        lock (SharedLock)
        {
            _shared = null;
            Current = null;
        }
    }

    private readonly object _lock = new();
    private ILogger<ServerPhaseTracker> _logger;
    private readonly Dictionary<BootStage, TaskCompletionSource> _stageSignals = new();

    private BootStage _completed = BootStage.None;

    public ServerPhaseTracker(ILogger<ServerPhaseTracker>? logger = null)
    {
        _logger = logger ?? NullLogger<ServerPhaseTracker>.Instance;

        foreach (BootStage stage in Enum.GetValues<BootStage>())
        {
            if (stage == BootStage.None || stage == BootStage.All)
                continue;
            if (!IsSingleFlag(stage))
                continue;
            _stageSignals[stage] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public BootStage CompletedStages
    {
        get
        {
            lock (_lock)
                return _completed;
        }
    }

    public bool IsComplete(BootStage stage)
    {
        lock (_lock)
            return (_completed & stage) == stage;
    }

    public event Action<BootStage>? StageCompleted;

    public void MarkComplete(BootStage stage)
    {
        if (!IsSingleFlag(stage))
            throw new ArgumentException(
                $"MarkComplete requires a single flag, got {stage}. Call MarkComplete per stage.",
                nameof(stage)
            );

        TaskCompletionSource? tcs;
        bool justAdvanced;

        lock (_lock)
        {
            justAdvanced = (_completed & stage) != stage;
            if (!justAdvanced)
                return;

            _completed |= stage;
            tcs = _stageSignals.GetValueOrDefault(stage);
        }

        _logger.LogInformation("Boot stage complete: {Stage}", Describe(stage));
        tcs?.TrySetResult();
        StageCompleted?.Invoke(stage);
    }

    /// <summary>
    /// A phrase a reader of the live boot log can act on. The bare enum name
    /// ("Network") reads as "networking is done", but this stage only covers
    /// external-IP/UPnP discovery — connectivity strategy attempts run
    /// separately and can still be in progress when this fires, which looked
    /// like an out-of-order or premature completion in the log.
    /// </summary>
    private static string Describe(BootStage stage) =>
        stage switch
        {
            BootStage.Network => "Network discovery (external IP, UPnP)",
            _ => stage.ToString(),
        };

    public async Task WhenReachedAsync(BootStage stage, CancellationToken ct)
    {
        if (IsComplete(stage))
            return;

        List<Task> pending = [];
        lock (_lock)
        {
            foreach ((BootStage flag, TaskCompletionSource signal) in _stageSignals)
            {
                if ((stage & flag) == flag && (_completed & flag) != flag)
                    pending.Add(signal.Task);
            }
        }

        if (pending.Count == 0)
            return;

        await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
    }

    private static bool IsSingleFlag(BootStage stage)
    {
        int value = (int)stage;
        return value != 0 && (value & (value - 1)) == 0;
    }
}
