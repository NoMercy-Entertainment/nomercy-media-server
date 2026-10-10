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

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Core.Resources;
using NoMercyQueue.Workers;

namespace NoMercyQueue;

public class QueueRunner
{
    private sealed class WorkerEntry
    {
        public int Count { get; set; }
        public List<QueueWorker> WorkerInstances { get; } = [];
        public CancellationTokenSource Cts { get; set; } = new();
        public bool IsUpdating { get; set; }

        public WorkerEntry(int count)
        {
            Count = count;
        }
    }

    private readonly object _workersLock = new();

    private readonly Dictionary<string, WorkerEntry> _workers;

    private volatile bool _isInitialized;

    // Guards the check-and-set of _isInitialized. Initialize() is invoked from
    // several boot paths (bootstrapper, deferred init, HTTPS/port rebuild) that can
    // overlap; without this a plain check-then-set let two callers both pass the
    // guard and spawn duplicate worker sets.
    private readonly Lock _initializationLock = new();

    private readonly ConcurrentDictionary<string, Thread> _activeWorkerThreads = new();

    private readonly JobQueue _jobQueue;
    public readonly JobDispatcher Dispatcher;
    private readonly IConfigurationStore? _configurationStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<QueueRunner> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly NoMercy.NmSystem.Lifecycle.IServerPhaseTracker? _phaseTracker;
    private readonly IResourceBudget? _resourceBudget;
    private readonly IReadOnlySet<string> _resourceAwareQueues;
    private readonly IWorkerActivityGate? _activityGate;
    private readonly IReadOnlyDictionary<
        string,
        NoMercy.NmSystem.Lifecycle.BootStage
    > _queueReadyStages;

    /// <summary>
    /// Static accessor for non-DI code paths (jobs, logic classes).
    /// Set during DI registration, before Initialize() is called.
    /// </summary>
    public static QueueRunner? Current { get; private set; }

    /// <summary>
    /// Exposes the underlying <see cref="JobQueue"/> for coordinator jobs that
    /// need to enqueue continuation work without going through the dispatcher's
    /// deduplication path.
    /// </summary>
    public JobQueue Queue => _jobQueue;

    public QueueRunner(
        IQueueContext queueContext,
        QueueConfiguration configuration,
        ILoggerFactory loggerFactory,
        IConfigurationStore? configurationStore = null,
        IServiceScopeFactory? scopeFactory = null,
        NoMercy.NmSystem.Lifecycle.IServerPhaseTracker? phaseTracker = null,
        IResourceBudget? resourceBudget = null,
        IReadOnlySet<string>? resourceAwareQueues = null,
        IWorkerActivityGate? activityGate = null,
        IReadOnlyDictionary<string, NoMercy.NmSystem.Lifecycle.BootStage>? queueReadyStages = null
    )
    {
        _loggerFactory = loggerFactory;
        _configurationStore = configurationStore;
        _scopeFactory = scopeFactory;
        _phaseTracker = phaseTracker;
        _resourceBudget = resourceBudget;
        _resourceAwareQueues = resourceAwareQueues ?? new HashSet<string>();
        _activityGate = activityGate;
        _queueReadyStages =
            queueReadyStages ?? new Dictionary<string, NoMercy.NmSystem.Lifecycle.BootStage>();
        _logger = loggerFactory.CreateLogger<QueueRunner>();
        _jobQueue = new(
            queueContext,
            configuration.MaxAttempts,
            loggerFactory.CreateLogger<JobQueue>()
        );
        Dispatcher = new(_jobQueue, loggerFactory.CreateLogger<JobDispatcher>());

        _workers = new();
        foreach (KeyValuePair<string, int> entry in configuration.WorkerCounts)
        {
            _workers[entry.Key] = new(entry.Value);
        }

        _logger.LogInformation(
            "QueueRunner constructed with WorkerCounts: {Counts}",
            string.Join(", ", configuration.WorkerCounts.Select(kvp => $"{kvp.Key}={kvp.Value}"))
        );

        Current = this;
    }

    public async Task Initialize()
    {
        lock (_initializationLock)
        {
            if (_isInitialized)
            {
                _logger.LogDebug("QueueRunner.Initialize() skipped — already initialized");
                return;
            }

            _isInitialized = true;
        }

        _jobQueue.ResetAllReservedJobs();

        int workerCount = 0;
        Dictionary<string, int> spawnedPerQueue = new();
        foreach (KeyValuePair<string, WorkerEntry> keyValuePair in _workers)
        {
            int target = keyValuePair.Value.Count;
            for (int i = 0; i < target; i++)
            {
                SpawnWorkerThread(keyValuePair.Key);
                workerCount++;
            }
            spawnedPerQueue[keyValuePair.Key] = target;
        }

        _logger.LogInformation(
            "Queue workers spawned per queue: {Counts} (total {Total})",
            [
                string.Join(", ", spawnedPerQueue.Select(kvp => $"{kvp.Key}={kvp.Value}")),
                workerCount,
            ]
        );

        // Restore any queues that were persisted as paused before the last shutdown.
        if (_configurationStore is not null)
        {
            List<string> queueNames;
            lock (_workersLock)
            {
                queueNames = [.. _workers.Keys];
            }

            foreach (string queueName in queueNames)
            {
                if (IsPersistedPaused(queueName))
                {
                    await Stop(queueName);
                    _logger.LogInformation(
                        "Queue '{Name}' restored as paused from configuration store",
                        queueName
                    );
                }
            }
        }

        // Signal that queue workers are ready, allowing cron jobs to start execution
        CronWorker.SignalQueueWorkersReady();

        await Task.CompletedTask;
    }

    private void SpawnWorkerThread(string name)
    {
        string threadKey = $"{name}-{Guid.NewGuid():N}";
        Thread thread = new(() =>
        {
            try
            {
                SpawnWorker(name);
            }
            catch (Exception ex)
            {
                _logger.LogError("Worker {Name} crashed: {Message}", [name, ex.Message]);
            }
            finally
            {
                _activeWorkerThreads.TryRemove(threadKey, out _);
            }
        })
        {
            IsBackground = true,
            Name = $"QueueWorker-{threadKey}",
            Priority = ThreadPriority.Lowest,
        };

        _activeWorkerThreads.TryAdd(threadKey, thread);
        thread.Start();
    }

    private void SpawnWorker(string name)
    {
        IResourceBudget? budget = _resourceAwareQueues.Contains(name) ? _resourceBudget : null;

        QueueWorker queueWorkerInstance = new(
            _jobQueue,
            name: name,
            runner: this,
            logger: _loggerFactory.CreateLogger<QueueWorker>(),
            scopeFactory: _scopeFactory,
            phaseTracker: _phaseTracker,
            resourceBudget: budget,
            resourceAwareQueues: _resourceAwareQueues,
            activityGate: _activityGate,
            readyStage: _queueReadyStages.GetValueOrDefault(
                name,
                NoMercy.NmSystem.Lifecycle.BootStage.All
            )
        );

        queueWorkerInstance.WorkCompleted += QueueWorkerCompleted(name, queueWorkerInstance);

        lock (_workersLock)
        {
            _workers[name].WorkerInstances.Add(queueWorkerInstance);
        }

        if (!IsPersistedPaused(name))
            queueWorkerInstance.Start();
    }

    private bool IsPersistedPaused(string name)
    {
        if (_configurationStore is null)
            return false;

        string key = $"queue.{name}.paused";
        return _configurationStore.HasKey(key) && _configurationStore.GetValue(key) == "true";
    }

    #region MyRegion

    public Task Start(string name)
    {
        List<QueueWorker> snapshot;
        lock (_workersLock)
        {
            snapshot = [.. _workers[name].WorkerInstances];
        }

        foreach (QueueWorker workerInstance in snapshot)
            workerInstance.Start();

        // Self-heal: if this queue somehow has fewer live instances than its
        // configured target (for example after a crash/dispose path), spawn the
        // missing workers here instead of leaving Resume/Start as a no-op.
        UpdateRunningWorkerCounts(name);

        return Task.CompletedTask;
    }

    public Task StartAll()
    {
        List<string> keys;
        lock (_workersLock)
        {
            keys = [.. _workers.Keys];
        }

        foreach (string key in keys)
            Start(key);

        return Task.CompletedTask;
    }

    public Task Stop(string name)
    {
        List<QueueWorker> snapshot;
        lock (_workersLock)
        {
            snapshot = [.. _workers[name].WorkerInstances];
        }

        foreach (QueueWorker workerInstance in snapshot)
            workerInstance.Stop();

        return Task.CompletedTask;
    }

    public Task StopAll()
    {
        List<string> keys;
        lock (_workersLock)
        {
            keys = [.. _workers.Keys];
        }

        foreach (string key in keys)
            Stop(key);

        return Task.CompletedTask;
    }

    public Task Restart(string name)
    {
        List<QueueWorker> snapshot;
        lock (_workersLock)
        {
            snapshot = [.. _workers[name].WorkerInstances];
        }

        foreach (QueueWorker workerInstance in snapshot)
            workerInstance.Restart();

        return Task.CompletedTask;
    }

    public Task RestartAll()
    {
        List<string> keys;
        lock (_workersLock)
        {
            keys = [.. _workers.Keys];
        }

        foreach (string key in keys)
            Restart(key);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop workers for <paramref name="name"/> and persist the paused state so
    /// it survives a server restart. Use this for user-initiated pauses.
    /// </summary>
    public async Task Pause(string name)
    {
        await Stop(name);

        if (_configurationStore is not null)
            await _configurationStore.SetValueAsync($"queue.{name}.paused", "true");

        _logger.LogInformation("Queue '{Name}' paused and state persisted", name);
    }

    /// <summary>
    /// Restart workers for <paramref name="name"/> and clear the persisted paused state.
    /// Use this for user-initiated resumes.
    /// </summary>
    public async Task Resume(string name)
    {
        // Resume should recover from a stale/stuck poll loop, not only from an
        // explicit pause. Restart existing instances and then let Start() heal
        // any missing worker instances for this queue.
        await Restart(name);
        await Start(name);

        if (_configurationStore is not null)
            await _configurationStore.SetValueAsync($"queue.{name}.paused", "false");

        _logger.LogInformation("Queue '{Name}' resumed and state persisted", name);
    }

    /// <summary>
    /// Persisted paused state for <paramref name="name"/>. Reads from the
    /// configuration store rather than tracking it in-memory so the
    /// dashboard sees the same value that was used to restore the queue at
    /// boot time (otherwise a server restart while paused leaves the UI
    /// showing "running" while the worker is actually stopped).
    /// </summary>
    public bool IsPaused(string name)
    {
        // A queue whose ready stage extends beyond BootStage.All (the encoder
        // queues, which also wait on Hardware detection) reports as paused while
        // that extra stage is still pending. This surfaces the startup hold in
        // the dashboard without persisting it as a user-initiated pause.
        if (
            _queueReadyStages.TryGetValue(name, out NoMercy.NmSystem.Lifecycle.BootStage readyStage)
        )
        {
            NoMercy.NmSystem.Lifecycle.BootStage extra =
                readyStage & ~NoMercy.NmSystem.Lifecycle.BootStage.All;
            if (
                extra != NoMercy.NmSystem.Lifecycle.BootStage.None
                && _phaseTracker is { } pt
                && !pt.IsComplete(extra)
            )
                return true;
        }

        if (_configurationStore is null)
            return false;

        string key = $"queue.{name}.paused";
        return _configurationStore.HasKey(key)
            && string.Equals(
                _configurationStore.GetValue(key),
                "true",
                StringComparison.OrdinalIgnoreCase
            );
    }

    #endregion


    private WorkCompletedEventHandler QueueWorkerCompleted(string name, QueueWorker instance)
    {
        return (_, _) =>
        {
            lock (_workersLock)
            {
                if (!ShouldRemoveWorker(name))
                    return;

                instance.Stop();
                _workers[name].WorkerInstances.Remove(instance);
            }
        };
    }

    private bool ShouldRemoveWorker(string name)
    {
        return _workers[name].WorkerInstances.Count > _workers[name].Count;
    }

    private void UpdateRunningWorkerCounts(string name)
    {
        int spawned;
        int targetCount;
        CancellationToken token;
        lock (_workersLock)
        {
            if (ShouldRemoveWorker(name))
                return;
            spawned = _workers[name].WorkerInstances.Count;
            targetCount = _workers[name].Count;
            token = _workers[name].Cts.Token;
        }

        Task workerTask = Task.Run(
            async () =>
            {
                while (spawned < targetCount)
                {
                    bool isUpdating;
                    lock (_workersLock)
                    {
                        isUpdating = _workers[name].IsUpdating;
                    }

                    if (isUpdating || spawned >= targetCount)
                        break;

                    SpawnWorkerThread(name);
                    spawned += 1;

                    await Task.Delay(100, token);
                }
            },
            token
        );

        workerTask.ContinueWith(
            t =>
                _logger.LogError(
                    "UpdateRunningWorkerCounts for {Name} failed: {Message}",
                    [name, t.Exception?.GetBaseException().Message]
                ),
            TaskContinuationOptions.OnlyOnFaulted
        );
    }

    public async Task<bool> SetWorkerCount(string name, int max, Guid? userId)
    {
        bool exists;
        lock (_workersLock)
        {
            exists = _workers.ContainsKey(name);
            if (exists && _workers[name].Count == max)
                return true;
        }

        if (!exists)
            return false;

        if (_configurationStore is not null)
        {
            await _configurationStore.SetValueAsync($"{name}Runners", max.ToString(), userId);
        }

        _logger.LogInformation("Setting queue {Name} to {Max} workers", [name, max]);

        CancellationToken token;
        lock (_workersLock)
        {
            WorkerEntry entry = _workers[name];
            entry.IsUpdating = true;
            entry.Cts.Cancel();
            entry.Count = max;
            entry.Cts = new();
            token = entry.Cts.Token;
        }

        await Task.Run(
            () =>
            {
                lock (_workersLock)
                {
                    _workers[name].IsUpdating = false;
                }
                UpdateRunningWorkerCounts(name);
            },
            token
        );

        return true;
    }

    public int GetWorkerIndex(string name, QueueWorker queueWorker)
    {
        lock (_workersLock)
        {
            return _workers[name].WorkerInstances.IndexOf(queueWorker);
        }
    }

    public IReadOnlyDictionary<string, Thread> GetActiveWorkerThreads()
    {
        return _activeWorkerThreads;
    }

    /// <summary>
    /// Counts worker instances that are *currently processing a job*
    /// (i.e. <see cref="QueueWorker.IsProcessingJob"/> is true) under
    /// queue names matched by <paramref name="namePredicate"/>.
    ///
    /// <para>This is what callers asking "is the queue busy?" actually
    /// want — <see cref="GetActiveWorkerThreads"/> reports every spawned
    /// thread for its lifetime, including workers blocked on
    /// <c>queue.ReserveJob</c> waiting for the next item, which over-
    /// counts dramatically.</para>
    /// </summary>
    public int CountWorkersProcessingJob(Func<string, bool> namePredicate)
    {
        int count = 0;
        lock (_workersLock)
        {
            foreach (KeyValuePair<string, WorkerEntry> entry in _workers)
            {
                if (!namePredicate(entry.Key))
                    continue;
                foreach (QueueWorker worker in entry.Value.WorkerInstances)
                    if (worker.IsProcessingJob)
                        count++;
            }
        }
        return count;
    }
}
