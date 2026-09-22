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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginSchedulerContractTests
{
    [Fact]
    public void A_job_carries_a_name_an_i18n_label_and_a_cron()
    {
        PluginScheduledJob job = new()
        {
            Name = "transfers",
            LabelKey = "torrent.job.transfers",
            CronExpression = "*/5 * * * *",
            RunAsync = (_, _) => Task.CompletedTask,
        };

        job.LabelKey.Should().Be("torrent.job.transfers");
        job.CronExpression.Should().Be("*/5 * * * *");
        job.AllowConcurrent.Should()
            .BeFalse(
                "a job still running at its next tick is behind, and a second copy writes the same file twice"
            );
    }

    [Fact]
    public void A_one_shot_job_takes_a_moment_not_a_cron()
    {
        MethodInfo runOnce = typeof(IPluginScheduler).GetMethod("RunOnceAsync")!;

        runOnce
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should()
            .Equal(typeof(string), typeof(DateTimeOffset), typeof(CancellationToken));
    }

    [Fact]
    public void Run_now_answers_a_job_id_rather_than_awaiting_the_cycle()
    {
        typeof(IPluginScheduler)
            .GetMethod("RunNowAsync")!
            .ReturnType.Should()
            .Be(
                typeof(Task<JobId>),
                "the torrent plugin awaited a 29-minute cycle inside an HTTP request"
            );
    }

    [Fact]
    public void A_worker_is_supervised_and_says_how_often_it_may_restart()
    {
        PluginWorker worker = new()
        {
            Name = "dht",
            LabelKey = "torrent.worker.dht",
            RunAsync = (_, _) => Task.CompletedTask,
        };

        worker.RestartOnCrash.Should().BeTrue();
        worker.MaxRestartsPerHour.Should().Be(3);
    }

    [Fact]
    public void Worker_state_is_readable_so_a_settings_page_can_show_it()
    {
        Enum.GetNames<PluginWorkerState>()
            .Should()
            .BeEquivalentTo(["Stopped", "Running", "Restarting", "Disabled"]);
    }

    [Fact]
    public void A_worker_that_keeps_crashing_degrades_and_names_the_restart_budget()
    {
        PluginRefusal refusal = PluginRefusalMessages.WorkerCrashed(
            "Torrent Downloader 1.0.0",
            "dht",
            3
        );

        refusal.Code.Should().Be(PluginRefusalCodes.SchedulerWorkerCrashed);
        refusal
            .Severity.Should()
            .Be(
                PluginRefusalSeverity.Degraded,
                "one worker giving up is not a reason to stop the whole plugin"
            );
        refusal.Why.Should().Contain("three times in an hour");
        refusal.What.Should().Contain("dht");
    }

    [Fact]
    public void Starting_and_stopping_a_worker_name_the_worker_not_a_handle()
    {
        typeof(IPluginScheduler)
            .GetMethod("StopWorkerAsync")!
            .GetParameters()[0]
            .ParameterType.Should()
            .Be(typeof(string), "a plugin that restarts holds no handle from before the restart");
        typeof(IPluginScheduler)
            .GetMethod("WorkerStateAsync")!
            .ReturnType.Should()
            .Be(typeof(Task<PluginWorkerState>));
    }
}
