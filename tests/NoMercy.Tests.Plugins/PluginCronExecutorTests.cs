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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hooks;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginCronExecutorTests
{
    private sealed class FakeTask : IScheduledTaskPlugin
    {
        public bool Ran;

        public string Name => "t";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.Parse("0H248H248H248H248H248H248H");
        public Version Version { get; } = new(1, 0);
        public string CronExpression => "*/5 * * * *";

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            Ran = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Adapter_ExposesPluginScheduleAndExecutes()
    {
        FakeTask task = new();
        PluginCronExecutor executor = new(task);

        Assert.Equal("plugin:0H248H248H248H248H248H248H", executor.JobName);
        Assert.Equal("*/5 * * * *", executor.CronExpression);

        await executor.ExecuteAsync(string.Empty, CancellationToken.None);

        Assert.True(task.Ran);
    }

    [Fact]
    public async Task Adapter_ForwardsCancellationToken_IgnoringParametersString()
    {
        FakeCancellationObservingTask task = new();
        PluginCronExecutor executor = new(task);
        using CancellationTokenSource cts = new();

        await executor.ExecuteAsync("ignored-parameters", cts.Token);

        Assert.Equal(cts.Token, task.ReceivedToken);
    }

    private sealed class FakeCancellationObservingTask : IScheduledTaskPlugin
    {
        public CancellationToken ReceivedToken;

        public string Name => "observer";
        public string Description => "d";
        public Ulid Id { get; } = Ulid.NewUlid();
        public Version Version { get; } = new(1, 0);
        public string CronExpression => "0 * * * *";

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public Task ExecuteAsync(CancellationToken ct = default)
        {
            ReceivedToken = ct;
            return Task.CompletedTask;
        }
    }
}
