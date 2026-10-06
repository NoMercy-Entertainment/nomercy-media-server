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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Lifecycle;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Core.Resources;
using BootStage = NoMercy.NmSystem.Lifecycle.BootStage;
using Exception = System.Exception;

namespace NoMercyQueue.Workers;

public class QueueWorker(
    JobQueue queue,
    string name = "default",
    QueueRunner? runner = null,
    ILogger<QueueWorker>? logger = null,
    IServiceScopeFactory? scopeFactory = null,
    IServerReadinessGate? readinessGate = null,
    IServerPhaseTracker? phaseTracker = null,
    IResourceBudget? resourceBudget = null,
    IReadOnlySet<string>? resourceAwareQueues = null,
    IWorkerActivityGate? activityGate = null,
    BootStage readyStage = BootStage.All
)
{
    private static readonly TimeSpan BudgetRetryDelay = TimeSpan.FromSeconds(5);

    private const int MaxTransientRetries = 5;
    private const int TransientRetryBaseMs = 3000;
    private const int TransientRetryJitterMs = 2000;

    private long? _currentJobId;
    private bool _isRunning = true;
    private CancellationTokenSource _stopCts = new();

    // The currently-running poll loop task. Restart() must let the previous loop
    // fully exit before the next one starts polling, or the two overlap and share
    // _currentJobId — reserving/running the same job twice.
    private Task? _runTask;

    /// <summary>
    /// Set when the resource budget gate rejected the most recent acquire
    /// attempt and cleared on the next successful acquire. Used to log the
    /// "budget saturated, retrying" line ONCE per saturation episode instead
    /// of every <see cref="BudgetRetryDelay"/> tick — multiple workers
    /// polling against a fully-leased semaphore otherwise spam the log with
    /// retry notices for the entire duration of the holding ffmpeg.
    /// </summary>
    private bool _suppressBudgetSaturationLog;
    private int _saturationRetryCount;
    private const int SaturationLogInterval = 120; // Log every 10 minutes (120 * 5s)

    private int CurrentIndex => runner?.GetWorkerIndex(name, this) ?? -1;

    /// <summary>
    /// True while this worker holds a reserved job (between
    /// <c>queue.ReserveJob</c> and <c>queue.DeleteJob</c>). False when
    /// the worker thread is alive but waiting for the next job — a
    /// spawned worker is not the same as a busy worker.
    ///
    /// <para>Consumed by the encoder's hardware-benchmark deferral
    /// (see <c>EncoderActivityProbe</c>) so calibration runs only when
    /// the queue is genuinely idle, not just whenever a worker thread
    /// happens to exist.</para>
    /// </summary>
    public bool IsProcessingJob => _currentJobId is not null;

    public event WorkCompletedEventHandler WorkCompleted = delegate { };

    public async Task StartAsync(CancellationToken stopToken)
    {
        if (phaseTracker is not null)
        {
            // Per-worker poll-loop lines were ~one per worker thread; the single
            // "Queue workers spawned per queue" summary in QueueRunner covers this.
            await phaseTracker.WhenReachedAsync(readyStage, stopToken).ConfigureAwait(false);
        }
        else if (readinessGate is not null)
        {
            await readinessGate.WaitForReadyAsync(stopToken).ConfigureAwait(false);
        }

        if (stopToken.IsCancellationRequested)
            return;

        while (_isRunning && !stopToken.IsCancellationRequested)
        {
            // Playback-activity gate: while a user is actively streaming, defer
            // reserving new jobs for NAS-read-heavy queues (library/file/import/
            // extras) so segment serves don't compete with background scans for
            // the same NFS mount. Nothing is reserved or released here — this
            // runs BEFORE ReserveJob, so it never disturbs the resource-budget
            // gate or dequeue ordering below.
            if (activityGate is not null && activityGate.ShouldDefer(name))
            {
                if (stopToken.WaitHandle.WaitOne(activityGate.DeferInterval))
                    break;

                continue;
            }

            QueueJobModel? job = queue.ReserveJob(name, _currentJobId);

            if (job != null)
            {
                // Resource-budget gate: for resource-aware queues, check
                // whether the budget has a free slot before executing. If
                // not, release the reservation and let the job be picked up
                // again after a short delay.
                //
                // Dispatch order itself needs no gate here: encoder-task's
                // own ReserveJobQuery (Priority DESC, Id ASC) already governs
                // which child runs next among what has been decomposed, and
                // the "encoder" queue's own reserve governs which coordinator
                // decomposes next. A cross-queue peek that also deferred to
                // an eligible-but-undecomposed coordinator livelocked every
                // encoder-task worker whenever the single coordinator thread
                // was busy elsewhere — coordinators and children never
                // compete for the same execution slot, so there is nothing
                // for a peek across them to correctly arbitrate.
                ResourceLease? lease = null;

                if (resourceBudget is not null && resourceAwareQueues?.Contains(name) == true)
                {
                    BudgetAcquireOutcome outcome = TryAcquireBudget(job, out lease);

                    if (
                        outcome == BudgetAcquireOutcome.GpuDeviceAbsent
                        && TryDegradeToSoftware(job)
                    )
                    {
                        // Job was re-planned onto its now-CPU queue with a
                        // software ResourceRequirement and requeued under its
                        // original row — this reservation must be dropped
                        // here (not released back to this queue) so it isn't
                        // picked up again; Requeue already signalled
                        // WorkAvailable for the destination queue's workers.
                        continue;
                    }

                    if (outcome != BudgetAcquireOutcome.Granted)
                    {
                        queue.ReleaseReservation(job, BudgetRetryDelay);

                        // Honor the full retry interval — using WorkAvailable
                        // here would let an unrelated Enqueue wake us up early
                        // and immediately re-probe the budget, which spins
                        // through deferred jobs at DB-query rate when many
                        // are stacked up under headroom denial. WaitHandle on
                        // the stop token keeps the sleep cancellation-aware.
                        if (stopToken.WaitHandle.WaitOne(BudgetRetryDelay))
                            break;

                        continue;
                    }
                }

                _currentJobId = job.Id;

                try
                {
                    object jobWithArguments = SerializationHelper.Deserialize<object>(job.Payload);

                    if (jobWithArguments is IShouldQueue classInstance)
                    {
                        IShouldQueue executed = ExecuteWithTransientRetry(classInstance, job);

                        // A coordinator that rewrote its own row is still queued —
                        // deleting it here would drop the encode mid-flight.
                        if (executed is not ISelfRescheduling { RescheduledInPlace: true })
                            queue.DeleteJob(job);

                        _currentJobId = null;
                        OnWorkCompleted(EventArgs.Empty);

                        logger?.LogTrace(
                            "QueueWorker {Name} - {CurrentIndex}: Job {JobId} of Type {ClassInstance} processed successfully",
                            [name, CurrentIndex, job.Id, classInstance]
                        );
                    }
                    else
                    {
                        string typeName = jobWithArguments.GetType().FullName ?? "null";
                        logger?.LogError(
                            "QueueWorker {Name} - {CurrentIndex}: Job {JobId} deserialized to {TypeName} which does not implement IShouldQueue — rejecting",
                            [name, CurrentIndex, job.Id, typeName]
                        );

                        queue.FailJob(
                            job,
                            new InvalidOperationException(
                                $"Job payload deserialized to {typeName} which does not implement IShouldQueue"
                            )
                        );
                        _currentJobId = null;
                    }
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    // Shutdown interrupted the job (e.g. an encode whose ffmpeg
                    // child was torn down via the host-stopping token). This is
                    // NOT a failure: return the reservation to the pool, which
                    // also undoes the attempt increment from GetNextJob, so the
                    // job resumes cleanly on next boot instead of burning a retry
                    // or dead-lettering after a few restarts. Then exit the loop.
                    queue.ReleaseReservation(job, TimeSpan.Zero);
                    _currentJobId = null;

                    logger?.LogInformation(
                        "QueueWorker {Name} - {CurrentIndex}: Job {JobId} interrupted by shutdown — released for retry",
                        [name, CurrentIndex, job.Id]
                    );
                    break;
                }
                catch (ObjectDisposedException)
                    when (stopToken.IsCancellationRequested || CannotRunAnotherJob())
                {
                    // The service provider this worker scopes jobs against is gone
                    // (ExecuteWithTransientRetry → IServiceScopeFactory.CreateScope).
                    // Whatever disposed it, this worker can never run another job, so
                    // the job is not at fault: release the reservation so a live worker
                    // or the next boot picks it up, and leave the loop.
                    //
                    // Two ways to be sure of that, and both are needed. Shutdown
                    // signals it directly. The HTTPS restart does not: it disposes the
                    // setup host's container while its workers are still polling and
                    // were never signalled, so the cancellation-only form dead-lettered
                    // every one of those jobs — 367 on a first boot, including the scan
                    // for a library added after the restart. Asking the provider itself
                    // covers that. Neither condition holding means the provider is
                    // alive and the job threw this on its own, which must spend an
                    // attempt: no condition at all released such a job forever.
                    queue.ReleaseReservation(job, TimeSpan.Zero);
                    _currentJobId = null;

                    logger?.LogInformation(
                        "QueueWorker {Name} - {CurrentIndex}: Job {JobId} scope disposed — released for retry",
                        [name, CurrentIndex, job.Id]
                    );
                    break;
                }
                catch (Exception ex)
                {
                    queue.FailJob(job, ex);

                    _currentJobId = null;

                    logger?.LogError(
                        "QueueWorker {Name} - {CurrentIndex}: Job {JobId} of Type {Payload} failed with error: {Error}",
                        [name, CurrentIndex, job.Id, job.Payload, ex]
                    );
                }
                finally
                {
                    if (lease is not null)
                        resourceBudget?.Release(lease);
                }
            }
            else
            {
                OnWorkCompleted(EventArgs.Empty);

                try
                {
                    queue.WorkAvailable.Wait(TimeSpan.FromSeconds(5), stopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Outcome of a budget-gate probe. <see cref="Saturated"/> means the
    /// requirement is fine but no slot is free right now — worth retrying.
    /// <see cref="GpuDeviceAbsent"/> means the requirement pins a GPU device
    /// key that is not (and will never be) registered on this host — no
    /// amount of retrying opens that gate, so the caller must degrade the
    /// job instead.
    /// </summary>
    private enum BudgetAcquireOutcome
    {
        Granted,
        Saturated,
        GpuDeviceAbsent,
    }

    /// <summary>
    /// Attempts to acquire budget for the given job payload. Sets
    /// <paramref name="lease"/> when a slot is available.
    /// </summary>
    private BudgetAcquireOutcome TryAcquireBudget(QueueJobModel job, out ResourceLease? lease)
    {
        lease = null;

        if (resourceBudget is null)
            return BudgetAcquireOutcome.Granted;

        ResourceRequirement? requirement = ExtractRequirement(job);
        if (requirement is null)
            return BudgetAcquireOutcome.Granted;

        try
        {
            lease = resourceBudget.TryAcquire(requirement, TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            // The resource budget might throw if a GPU device key is unknown or other logic errors.
            // We treat this as a temporary "saturated" state so the job stays
            // in the queue until hardware detection completes or the requirement
            // changes.
            if (!_suppressBudgetSaturationLog)
            {
                logger?.LogWarning(
                    "[{Queue}] budget acquisition failed for job {JobId}: {Message}. Will retry every {Delay}s.",
                    [name, job.Id, ex.Message, BudgetRetryDelay.TotalSeconds]
                );
                _suppressBudgetSaturationLog = true;
            }

            return BudgetAcquireOutcome.Saturated;
        }

        if (lease is null)
        {
            // Log the FIRST saturated retry per episode, and then every
            // SaturationLogInterval retries thereafter. A long-running
            // ffmpeg bundle can hold the semaphore for tens of minutes; with
            // four workers polling every BudgetRetryDelay against a fully-
            // leased budget, an Info-level log per retry produces thousands
            // of identical lines that drown out the rest of the encoder log
            // and (via synchronous Serilog sinks) contribute to host
            // unresponsiveness. The flag resets on the next successful
            // acquire so a new saturation episode logs once again.
            _saturationRetryCount++;
            bool shouldLog =
                !_suppressBudgetSaturationLog
                || (_saturationRetryCount % SaturationLogInterval == 0);

            if (shouldLog)
            {
                int gpuAvailable = requirement.GpuDeviceKey is not null
                    ? resourceBudget.AvailableGpuEncoderSlots(requirement.GpuDeviceKey)
                    : -1;
                int cpuAvailable = resourceBudget.AvailableCpuThreads();

                logger?.LogInformation(
                    "[{Queue}] budget saturated for job {JobId} ({Requirement}) — GPU slots available: {Gpu}, CPU threads available: {Cpu}. Still retrying every {Delay}s.",
                    [
                        name,
                        job.Id,
                        requirement,
                        gpuAvailable,
                        cpuAvailable,
                        BudgetRetryDelay.TotalSeconds,
                    ]
                );
                _suppressBudgetSaturationLog = true;
            }

            // A busy-but-present GPU is worth retrying. A GPU device key that
            // isn't registered at all never opens — the requirement itself
            // must degrade rather than loop here forever.
            if (
                requirement.GpuDeviceKey is not null
                && !resourceBudget.IsGpuDeviceRegistered(requirement.GpuDeviceKey)
            )
            {
                return BudgetAcquireOutcome.GpuDeviceAbsent;
            }

            return BudgetAcquireOutcome.Saturated;
        }

        _suppressBudgetSaturationLog = false;
        _saturationRetryCount = 0;
        return BudgetAcquireOutcome.Granted;
    }

    /// <summary>
    /// Re-plans a job whose GPU requirement is permanently unsatisfiable (see
    /// <see cref="BudgetAcquireOutcome.GpuDeviceAbsent"/>) onto a software/CPU
    /// requirement and reroutes it to that job's own (now CPU) queue. Returns
    /// false when the job doesn't implement <see cref="IResourceDegradable"/>
    /// or has no software fallback — the caller keeps the original reservation
    /// in place and retries rather than dropping work it cannot confidently
    /// degrade.
    /// </summary>
    private bool TryDegradeToSoftware(QueueJobModel job)
    {
        object deserialized;
        try
        {
            deserialized = SerializationHelper.Deserialize<object>(job.Payload);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "[{Queue}] failed to deserialize job {JobId} while degrading an absent-GPU requirement",
                [name, job.Id]
            );
            return false;
        }

        if (deserialized is not IResourceDegradable degradable)
            return false;

        string? gpuDeviceKey = (deserialized as IHasResourceRequirement)
            ?.ResourceRequirement
            ?.GpuDeviceKey;

        IShouldQueue? degraded = degradable.DegradeToSoftware();
        if (degraded is null)
            return false;

        string newPayload = SerializationHelper.Serialize(degraded);

        logger?.LogWarning(
            "[{Queue}] job {JobId} requires GPU device '{GpuKey}' which is not present on this "
                + "host — degrading to software and rerouting to '{NewQueue}'.",
            [name, job.Id, gpuDeviceKey, degraded.QueueName]
        );

        queue.Requeue(job, degraded.QueueName, newPayload);
        return true;
    }

    /// <summary>
    /// Lightly deserializes the job payload to read just the
    /// <see cref="ResourceRequirement"/> from an <see cref="IHasResourceRequirement"/>
    /// job without executing the full deserialization path.
    /// Returns null when the job does not carry resource metadata.
    /// </summary>
    private static ResourceRequirement? ExtractRequirement(QueueJobModel job)
    {
        try
        {
            object deserialized = SerializationHelper.Deserialize<object>(job.Payload);
            if (deserialized is IHasResourceRequirement carrier)
                return carrier.ResourceRequirement;
        }
        catch
        {
            // Deserialization failure here is non-fatal — skip the gate.
        }

        return null;
    }

    protected virtual void OnWorkCompleted(EventArgs e)
    {
        WorkCompleted.Invoke(this, e);
    }

    /// <summary>
    /// Executes a job with transparent retry for transient SQLite errors (SQLITE_BUSY /
    /// "database is locked").  These retries do NOT consume the job's attempt count —
    /// they exist to absorb short-lived write-lock contention that is normal under
    /// concurrent queue workers sharing a single SQLite database.
    /// <para>
    /// When a <see cref="IServiceScopeFactory"/> is available and the job implements
    /// <see cref="IJobStorageInjector"/>, a fresh DI scope is opened per execution,
    /// storage services are resolved and set on the job, and the scope is disposed
    /// when the job completes (success or failure).
    /// </para>
    /// <para>Returns the instance that actually ran. Under DI that is the rebuilt
    /// job, not the one passed in, and post-run state the caller needs — see
    /// <see cref="ISelfRescheduling"/> — lives only on the instance that ran.</para>
    /// </summary>
    /// <summary>
    /// Whether this worker's service provider is gone, so no further job can be
    /// scoped and the one that just threw was not at fault.
    /// </summary>
    /// <remarks>
    /// Asked by creating a scope and throwing it away, because that is the exact
    /// operation the job path performs. With no factory at all there is nothing to
    /// dispose, so the exception came from the job itself.
    /// </remarks>
    private bool CannotRunAnotherJob()
    {
        if (scopeFactory is null)
            return false;

        try
        {
            using IServiceScope probe = scopeFactory.CreateScope();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private IShouldQueue ExecuteWithTransientRetry(IShouldQueue job, QueueJobModel queueJob)
    {
        IServiceScope? scope = null;

        if (scopeFactory is not null)
        {
            scope = scopeFactory.CreateScope();
            IServiceProvider serviceProvider = scope.ServiceProvider;

            // Rebuild the job through DI so constructor dependencies are injected,
            // then re-apply the serialized job data. Upstream deserialization yields
            // a data-only instance; services are resolved from the per-job scope.
            IShouldQueue rebuilt = (IShouldQueue)
                ActivatorUtilities.CreateInstance(serviceProvider, job.GetType());
            SerializationHelper.Populate(queueJob.Payload, rebuilt);
            job = rebuilt;

            // Back-compat: jobs not yet migrated to constructor injection still
            // receive their services via this post-construction hook.
            if (job is IJobStorageInjector injector)
                injector.InjectStorageServices(serviceProvider);
        }

        if (job is IJobIdReceiver idReceiver)
            idReceiver.ReceiveJobId((int)queueJob.Id);

        if (job is IJobAttemptReceiver attemptReceiver)
            attemptReceiver.ReceiveAttempt(queueJob.Attempts, queue.MaxAttempts);

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    job.Handle().GetAwaiter().GetResult();
                    return job;
                }
                catch (Exception ex)
                    when (IsTransientSqliteError(ex) && attempt < MaxTransientRetries)
                {
                    int delay = TransientRetryBaseMs + Random.Shared.Next(TransientRetryJitterMs);

                    logger?.LogWarning(
                        "QueueWorker {Name} - {CurrentIndex}: Job {JobId} hit transient SQLite error (attempt {Attempt}/{Max}), retrying in {Delay}ms",
                        [name, CurrentIndex, queueJob.Id, attempt + 1, MaxTransientRetries, delay]
                    );

                    Thread.Sleep(delay);
                }
            }
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private static bool IsTransientSqliteError(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            string typeName = current.GetType().Name;

            if (
                typeName is "SqliteException"
                && current.Message.Contains("is locked", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    public void Stop()
    {
        logger?.LogInformation(
            "QueueWorker {Name} - {CurrentIndex}: stopped",
            [name, CurrentIndex]
        );
        _isRunning = false;
        _stopCts.Cancel();
    }

    public void StopWhenReady()
    {
        while (_currentJobId != null)
            Thread.Sleep(1000);

        Stop();
    }

    /// <summary>
    /// Fire-and-forget wrapper that schedules <see cref="StartAsync"/> on the
    /// thread pool. <see cref="QueueRunner"/> uses this from its synchronous
    /// spawn/lifecycle methods so it doesn't have to await each worker
    /// individually — workers run for the lifetime of the process.
    /// </summary>
    public void Start()
    {
        Task? previous = _runTask;
        if (_stopCts.IsCancellationRequested)
            _stopCts = new();
        _isRunning = true;
        _runTask = Task.Run(async () =>
        {
            // If a prior loop is still winding down (Restart cancelled it while it
            // was mid-job), wait for it to exit before polling so two loops never
            // run concurrently on this instance. Bounded so a stuck job can't
            // deadlock the restart.
            if (previous is not null)
            {
                try
                {
                    await previous.WaitAsync(TimeSpan.FromSeconds(30));
                }
                catch (Exception)
                {
                    // Timed out or faulted — proceed; the old loop's token is
                    // already cancelled so it will not reserve new work.
                }
            }

            try
            {
                await StartAsync(_stopCts.Token);
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
                // Stop() during the boot-stage wait (e.g. the HTTP→HTTPS host swap's
                // StopAll) is an orderly shutdown — logging it as a critical crash
                // painted every setup boot red.
            }
            catch (Exception ex)
            {
                logger?.LogCritical(
                    ex,
                    "QueueWorker {Name} - {CurrentIndex}: StartAsync crashed",
                    [name, CurrentIndex]
                );
            }
        });
    }

    /// <summary>
    /// Cancels the current Stop signal and re-enters the poll loop. Used by
    /// <see cref="QueueRunner.Restart"/> so a paused worker can resume without
    /// reconstructing the instance (preserving its DI scope and event handlers).
    /// </summary>
    public void Restart()
    {
        Stop();
        Start();
    }
}
