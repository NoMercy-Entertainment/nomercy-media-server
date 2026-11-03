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

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue.Core.Interfaces;
using NoMercyQueue.Core.Models;
using NoMercyQueue.Workers;
using Xunit;

namespace NoMercy.Tests.Queue;

/// <summary>
/// A schedule the scheduler cannot parse must fail where the job is registered,
/// as one clear error log, and never start a worker (issue #399). Before this,
/// <c>RegisterJob</c>/<c>RegisterExecutor</c> threw the parser's exception at
/// the caller, and a DB row with a bad expression started a worker that ran the
/// job once, failed to compute its next run, and then re-ran it every minute
/// because <c>NextRun</c> never advanced.
/// </summary>
[Trait("Category", "Unit")]
public class CronWorkerUnparseableScheduleTests
{
    private const string UnparseableSchedule = "every monday at noon";

    private sealed class TestCronJobExecutor : ICronJobExecutor
    {
        public string CronExpression => UnparseableSchedule;
        public string JobName => "Unparseable Executor";

        public Task ExecuteAsync(
            string parameters,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<CronWorker>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static Dictionary<string, CancellationTokenSource> GetJobCancellationTokens(
        CronWorker worker
    ) =>
        (Dictionary<string, CancellationTokenSource>)
            typeof(CronWorker)
                .GetField("_jobCancellationTokens", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(worker)!;

    private static void SetRegisteredJobType(CronWorker worker, string jobType, Type executorType)
    {
        Dictionary<string, Type> registeredJobs =
            (Dictionary<string, Type>)
                typeof(CronWorker)
                    .GetField("_registeredJobs", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(worker)!;
        registeredJobs[jobType] = executorType;
    }

    private static void InvokeStartDatabaseJobWorkers(CronWorker worker) =>
        typeof(CronWorker)
            .GetMethod("StartDatabaseJobWorkers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(worker, null);

    private static void ShouldHaveLoggedTheBadSchedule(CapturingLogger logger, string jobName)
    {
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Error
                && entry.Message.Contains(jobName)
                && entry.Message.Contains(UnparseableSchedule)
            );
    }

    [Fact]
    public void RegisterJob_ScheduleTheSchedulerCannotParse_LogsAnErrorAndStartsNoWorker()
    {
        CapturingLogger logger = new();
        ServiceCollection services = new();
        services.AddScoped<TestCronJobExecutor>();
        using ServiceProvider provider = services.BuildServiceProvider();
        CronWorker worker = new(provider, logger, new TestQueueContextAdapter());

        Action act = () =>
            worker.RegisterJob<TestCronJobExecutor>(
                "bad-schedule-job",
                "Bad Schedule Job",
                UnparseableSchedule
            );

        act.Should().NotThrow();
        GetJobCancellationTokens(worker).Should().NotContainKey("bad-schedule-job");
        ShouldHaveLoggedTheBadSchedule(logger, "Bad Schedule Job");
    }

    [Fact]
    public void RegisterExecutor_ScheduleTheSchedulerCannotParse_LogsAnErrorAndStartsNoWorker()
    {
        CapturingLogger logger = new();
        ServiceCollection services = new();
        using ServiceProvider provider = services.BuildServiceProvider();
        CronWorker worker = new(provider, logger, new TestQueueContextAdapter());
        TestCronJobExecutor executor = new();

        Action act = () => worker.RegisterExecutor(executor);

        act.Should().NotThrow();
        GetJobCancellationTokens(worker).Should().NotContainKey(executor.JobName);
        ShouldHaveLoggedTheBadSchedule(logger, executor.JobName);
    }

    [Fact]
    public void StartDatabaseJobWorkers_RowWithAScheduleTheSchedulerCannotParse_LogsAnErrorAndStartsNoWorker()
    {
        CapturingLogger logger = new();
        TestQueueContextAdapter context = new();
        context.AddCronJob(
            new CronJobModel
            {
                Name = "Bad Schedule Row",
                CronExpression = UnparseableSchedule,
                JobType = "bad-schedule-type",
                IsEnabled = true,
                NextRun = DateTime.UtcNow.AddYears(-1),
            }
        );
        ServiceCollection services = new();
        services.AddScoped<TestCronJobExecutor>();
        using ServiceProvider provider = services.BuildServiceProvider();
        CronWorker worker = new(provider, logger, context);
        SetRegisteredJobType(worker, "bad-schedule-type", typeof(TestCronJobExecutor));

        InvokeStartDatabaseJobWorkers(worker);

        GetJobCancellationTokens(worker).Should().NotContainKey("bad-schedule-type");
        ShouldHaveLoggedTheBadSchedule(logger, "Bad Schedule Row");
    }
}
