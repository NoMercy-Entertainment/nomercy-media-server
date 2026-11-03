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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.NmSystem.Extensions;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Services;

namespace NoMercyQueue.Workers;

public class CronWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CronWorker> _logger;
    private readonly IQueueContext _queueContext;
    private readonly Dictionary<string, Type> _registeredJobs = new();

    // Runtime executor INSTANCES (e.g. one per loaded plugin) can't go through
    // _registeredJobs — that dictionary resolves an executor by DI type at fire
    // time, and a plugin instance was never registered in the container. Keyed
    // by JobName (== CronJobModel.JobType for these jobs); ExecuteJob checks
    // this map before falling back to DI resolution.
    private readonly Dictionary<string, ICronJobExecutor> _instanceExecutors = new();
    private readonly List<CronJobModel> _codeDefinedJobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly Dictionary<string, Task> _jobTasks = new();
    private readonly IEnumerable<CronJobRegistration> _registrations;

    private static readonly TaskCompletionSource<bool> QueueWorkersReadyTcs = new();
    private static readonly TaskCompletionSource<bool> DatabaseReadyTcs = new();

    /// <summary>
    /// Signal that queue workers have started and cron jobs can begin execution.
    /// Call this from QueueRunner.Initialize() after workers are spawned.
    /// </summary>
    public static void SignalQueueWorkersReady()
    {
        QueueWorkersReadyTcs.TrySetResult(true);
    }

    /// <summary>
    /// Returns a task that completes when the database is ready for queries.
    /// </summary>
    public static Task<bool> GetDatabaseReadyTask() => DatabaseReadyTcs.Task;

    /// <summary>
    /// Signal that the database has been migrated and is ready for queries.
    /// Call this from DatabaseSeeder after migrations complete.
    /// </summary>
    public static void SignalDatabaseReady(bool success = true)
    {
        if (success)
            DatabaseReadyTcs.TrySetResult(true);
        else
            DatabaseReadyTcs.TrySetResult(false);
    }

    public CronWorker(
        IServiceProvider serviceProvider,
        ILogger<CronWorker> logger,
        IQueueContext queueContext,
        IEnumerable<CronJobRegistration>? registrations = null
    )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _queueContext = queueContext;
        _registrations = registrations ?? [];
    }

    public void RegisterJob<T>(
        string jobType,
        string name,
        string cronExpression,
        object? parameters = null
    )
        where T : class, ICronJobExecutor
    {
        _registeredJobs[jobType] = typeof(T);

        CronJobModel job = new()
        {
            Name = name,
            CronExpression = cronExpression,
            JobType = jobType,
            Parameters = parameters != null ? JsonConvert.SerializeObject(parameters) : null,
            IsEnabled = true,
        };

        ScheduleCodeDefinedJob(job, DateTime.UtcNow);
    }

    public void RegisterJobWithSchedule<T>(string jobType, IServiceProvider serviceProvider)
        where T : class, ICronJobExecutor
    {
        _registeredJobs[jobType] = typeof(T);

        using IServiceScope scope = serviceProvider.CreateScope();
        T executor = scope.ServiceProvider.GetRequiredService<T>();

        DateTime currentTime = DateTime.UtcNow;

        // Individual registration logged at trace — summary logged in ExecuteAsync

        CronJobModel job = new()
        {
            Name = executor.JobName,
            CronExpression = executor.CronExpression,
            JobType = jobType,
            Parameters = null,
            IsEnabled = true,
            CreatedAt = currentTime,
        };

        ScheduleCodeDefinedJob(job, currentTime);
    }

    /// <summary>
    /// Parses the schedule once, here where the job is registered, so a
    /// schedule the scheduler cannot read is one clear error at registration
    /// instead of an exception out of the job's worker loop after its first run.
    /// A job whose schedule does not parse is not started.
    /// </summary>
    private void ScheduleCodeDefinedJob(CronJobModel job, DateTime now)
    {
        CronSchedule? schedule = ParseScheduleOrLog(job);
        if (schedule is null)
            return;

        job.NextRun = schedule.GetNextOccurrence(now);

        _codeDefinedJobs.Add(job);
        StartJobWorker(job);
    }

    private CronSchedule? ParseScheduleOrLog(CronJobModel job)
    {
        try
        {
            return CronSchedule.Parse(job.CronExpression);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Cron job {JobName} has a schedule the scheduler cannot parse: '{CronExpression}' ({Reason}); it will not run",
                [job.Name, job.CronExpression, ex.GetBaseException().Message]
            );
            return null;
        }
    }

    private void StartJobWorker(CronJobModel job)
    {
        if (
            _jobCancellationTokens.TryGetValue(
                job.JobType,
                out CancellationTokenSource? existingCts
            )
        )
        {
            _logger.LogDebug(
                "Worker already running for job: {JobName}, skipping duplicate registration",
                job.Name
            );
            return;
        }

        CancellationTokenSource cts = new();
        _jobCancellationTokens[job.JobType] = cts;

        Task task = Task.Run(async () => await JobWorkerLoop(job, cts.Token), cts.Token);
        _jobTasks[job.JobType] = task;

        // Per-job start logged at trace level only
    }

    private async Task JobWorkerLoop(CronJobModel job, CancellationToken cancellationToken)
    {
        // Wait for queue workers to be ready before starting cron job execution
        try
        {
            await QueueWorkersReadyTcs.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Job worker cancelled while waiting for queue workers: {JobName}",
                job.Name
            );
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Schedules are evaluated in UTC so Daily(n) means n:00 UTC on every host.
                DateTime currentTime = DateTime.UtcNow;

                // Check if it's time to run
                if (job.NextRun.HasValue && currentTime >= job.NextRun.Value)
                {
                    _logger.LogDebug(
                        "Executing cron job: {JobName} (Scheduled: {NextRun}, Current: {CurrentTime})",
                        [job.Name, job.NextRun, currentTime]
                    );

                    bool success = await ExecuteJob(job, currentTime, cancellationToken);

                    job.LastRun = currentTime;
                    // Advance NextRun on BOTH success and failure — without
                    // this, a failing cron job re-fires every 30 s (the loop's
                    // poll interval) until it succeeds, hammering the failing
                    // code path and flooding logs. Failures still surface via
                    // ExecuteJob's LogError catch; the schedule shouldn't
                    // accelerate as a side effect.
                    job.NextRun = CronService.GetNextOccurrence(job.CronExpression, currentTime);

                    if (success)
                    {
                        _logger.LogDebug(
                            "Successfully executed cron job: {JobName}. Next run: {NextRun}",
                            [job.Name, job.NextRun]
                        );
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Cron job {JobName} failed; rescheduling for {NextRun}",
                            [job.Name, job.NextRun]
                        );
                    }

                    // Update database whether the job succeeded or not — keeps
                    // the persisted NextRun aligned with the in-memory state.
                    UpdateDatabaseJob(job);
                }

                // Check every 30 seconds instead of 1 minute for better precision
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job worker cancelled for: {JobName}", job.Name);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in job worker for: {JobName}", job.Name);

                // Continue running even if there's an error
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            }
        }
    }

    private async Task<bool> ExecuteJob(
        CronJobModel job,
        DateTime currentTime,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (_instanceExecutors.TryGetValue(job.JobType, out ICronJobExecutor? instanceExecutor))
            {
                return await RunExecutor(instanceExecutor, job, cancellationToken);
            }

            if (!_registeredJobs.TryGetValue(job.JobType, out Type? jobExecutorType))
            {
                _logger.LogWarning(
                    "Job type {JobType} not registered for job {JobName}",
                    [job.JobType, job.Name]
                );
                return false;
            }

            using IServiceScope scope = _serviceProvider.CreateScope();
            ICronJobExecutor executor = (ICronJobExecutor)
                scope.ServiceProvider.GetRequiredService(jobExecutorType);

            return await RunExecutor(executor, job, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Job execution cancelled for: {JobName}", job.Name);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Job execution timed out for: {JobName}", job.Name);
            return false;
        }
        catch (Exception ex)
        {
            // Was LogDebug — invisible at default Information level, so cron
            // failures showed up as the bare "Failed to execute …" line with no
            // exception detail, leaving the operator no way to diagnose.
            _logger.LogError(
                ex,
                "Failed to execute cron job: {JobName} — {ErrorType}: {ErrorMessage}",
                [job.Name, ex.GetType().Name, ex.Message]
            );
            return false;
        }
    }

    private static async Task<bool> RunExecutor(
        ICronJobExecutor executor,
        CronJobModel job,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeoutCts = new(TimeSpan.FromMinutes(30));
        using CancellationTokenSource combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token
        );

        await executor.ExecuteAsync(job.Parameters.OrEmpty(), combinedCts.Token);
        return true;
    }

    private void UpdateDatabaseJob(CronJobModel job)
    {
        try
        {
            CronJobModel? dbJob = _queueContext.FindCronJobByName(job.Name);

            if (dbJob != null)
            {
                dbJob.LastRun = job.LastRun;
                dbJob.NextRun = job.NextRun;
                _queueContext.UpdateCronJob(dbJob);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update database for job: {JobName}", job.Name);
        }
    }

    private void RegisterDescriptor(CronJobRegistration registration)
    {
        _registeredJobs[registration.JobType] = registration.ExecutorType;

        using IServiceScope scope = _serviceProvider.CreateScope();
        ICronJobExecutor executor = (ICronJobExecutor)
            scope.ServiceProvider.GetRequiredService(registration.ExecutorType);

        string cronExpression = registration.CronExpression ?? executor.CronExpression;
        DateTime now = DateTime.UtcNow;

        CronJobModel job = new()
        {
            Name = executor.JobName,
            CronExpression = cronExpression,
            JobType = registration.JobType,
            Parameters = null,
            IsEnabled = true,
            CreatedAt = now,
        };

        ScheduleCodeDefinedJob(job, now);
    }

    // Schedules an executor INSTANCE directly — for runtime-discovered executors
    // (plugins) that have no DI registration to resolve by type. JobType is set
    // to the executor's own JobName so the instance registry lookup in
    // ExecuteJob and the dedup check in StartJobWorker key off the same value.
    public void RegisterExecutor(ICronJobExecutor executor)
    {
        _instanceExecutors[executor.JobName] = executor;

        DateTime now = DateTime.UtcNow;

        CronJobModel job = new()
        {
            Name = executor.JobName,
            CronExpression = executor.CronExpression,
            JobType = executor.JobName,
            Parameters = null,
            IsEnabled = true,
            CreatedAt = now,
        };

        ScheduleCodeDefinedJob(job, now);
    }

    /// <summary>
    /// Stops an instance-registered executor and lets go of it.
    /// <para>
    /// Registration is what a plugin's cron work hangs off, so without a way
    /// back out the worker keeps a reference to the plugin instance forever.
    /// That keeps the plugin's collectible load context alive after it is
    /// disabled, which on Windows means its files stay locked and it cannot be
    /// updated or removed without stopping the server.
    /// </para>
    /// </summary>
    public void RemoveExecutor(string jobName)
    {
        if (!_instanceExecutors.Remove(jobName))
            return;

        // Keyed by JobType, which for an instance executor is its JobName.
        if (_jobCancellationTokens.Remove(jobName, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        _jobTasks.Remove(jobName);
        _codeDefinedJobs.RemoveAll(job => job.Name == jobName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (CronJobRegistration registration in _registrations)
        {
            try
            {
                RegisterDescriptor(registration);
            }
            catch (Exception ex)
            {
                // A single unresolvable/misconfigured cron executor (e.g. a plugin
                // whose DI registration is missing) must NOT fault this
                // BackgroundService — that would trip the StopHost default and
                // refuse to boot the entire server. Skip it, keep the rest.
                _logger.LogError(
                    ex,
                    "Failed to register cron job {JobType}; skipping it so remaining jobs and the server still start",
                    registration.JobType
                );
            }
        }

        _logger.LogDebug(
            "Cron Worker started with {JobCount} registered jobs",
            _codeDefinedJobs.Count
        );

        // Wait for database migrations to complete before querying the database
        try
        {
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource combinedCts =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            bool dbReady = await DatabaseReadyTcs.Task.WaitAsync(combinedCts.Token);
            if (dbReady)
            {
                _logger.LogDebug("Database ready — loading database job workers");
                StartDatabaseJobWorkers();
            }
            else
            {
                _logger.LogWarning("Database seeding failed — skipping database job workers");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Cron Worker stopping before database was ready");
            return;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Timed out waiting for database readiness — skipping database job workers"
            );
        }

        // Keep the main service running
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cron Worker stopping...");
        }
    }

    private void StartDatabaseJobWorkers()
    {
        try
        {
            IReadOnlyList<CronJobModel> dbJobs = _queueContext.GetEnabledCronJobs();

            foreach (CronJobModel job in dbJobs)
            {
                if (
                    _registeredJobs.ContainsKey(job.JobType)
                    || _instanceExecutors.ContainsKey(job.JobType)
                )
                {
                    // A stored row is parsed before it gets a worker: unchecked,
                    // the loop would run the job once, fail to compute its next
                    // run, and re-run it every minute because NextRun never moved.
                    if (ParseScheduleOrLog(job) is not null)
                        StartJobWorker(job);
                }
                else
                {
                    _logger.LogWarning(
                        "Database job {JobName} has unregistered job type: {JobType}",
                        [job.Name, job.JobType]
                    );
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start database job workers");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all job workers...");

        // Cancel all job workers
        foreach (CancellationTokenSource cts in _jobCancellationTokens.Values)
        {
            await cts.CancelAsync();
        }

        // Wait for all workers to complete with a reduced timeout
        if (_jobTasks.Values.Count != 0)
        {
            try
            {
                await Task.WhenAll(_jobTasks.Values)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Some job workers did not stop within the timeout period");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Shutdown cancelled, forcing job worker termination");
            }
        }

        // Dispose resources
        foreach (CancellationTokenSource cts in _jobCancellationTokens.Values)
        {
            cts.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}
