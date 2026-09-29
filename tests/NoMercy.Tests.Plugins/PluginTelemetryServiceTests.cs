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

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Telemetry;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One slow SaaS call must never take the host down. An HttpClient timeout
/// inside the reporter surfaces as a <see cref="TaskCanceledException"/>,
/// which IS an <see cref="OperationCanceledException"/> — a catch that
/// excludes every <c>OperationCanceledException</c> lets a timeout escape
/// uncaught out of <c>ExecuteAsync</c>, faulting the BackgroundService and
/// tripping <c>BackgroundServiceExceptionBehavior.StopHost</c>.
/// </summary>
public class PluginTelemetryServiceTests
{
    private sealed class ThrowingSink : IPluginTelemetrySink
    {
        public int Calls;

        public Task SendAsync(PluginTelemetryReport report, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);

            // What an HttpClient.Timeout produces: the request's own linked
            // token fires, not the caller's — the caller's token is
            // untouched.
            throw new TaskCanceledException("timeout", new TimeoutException());
        }

        public Task SendInstallAsync(PluginInstallReport report, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static PluginTelemetryReporter MakeReporter(IPluginTelemetrySink sink)
    {
        Mock<IPluginManifestSource> plugins = new();
        plugins.Setup(p => p.All()).Returns([]);

        return new(
            plugins.Object,
            Mock.Of<IPluginRefusalCounter>(),
            Mock.Of<IPluginCrashCounter>(),
            Mock.Of<IPluginInstallFacts>(),
            sink,
            () => new(false),
            TimeProvider.System,
            Guid.NewGuid()
        );
    }

    [Fact]
    public async Task ExecuteAsync_WhenReporterTimesOut_SurvivesAndKeepsTicking()
    {
        ThrowingSink sink = new();

        PluginTelemetryService sut = new(
            MakeReporter(sink),
            TimeProvider.System,
            NullLogger<PluginTelemetryService>.Instance,
            intervalOverride: TimeSpan.FromMilliseconds(20)
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);

        // Proves the loop survived the first timeout: it reached a second
        // tick instead of ExecuteAsync's task faulting on the first one.
        SpinWait.SpinUntil(() => Volatile.Read(ref sink.Calls) >= 2, TimeSpan.FromSeconds(5))
            .Should()
            .BeTrue("the service must reach a second tick, not die on the first timeout");

        await cts.CancelAsync();

        // Before the fix, ExecuteAsync's task was faulted by the escaped
        // TaskCanceledException, and BackgroundService.StopAsync awaits
        // (and rethrows from) that task — this call would throw.
        Func<Task> stop = () => sut.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_WhenStoppingTokenIsCancelled_EndsWithoutFaulting()
    {
        ThrowingSink sink = new();

        PluginTelemetryService sut = new(
            MakeReporter(sink),
            TimeProvider.System,
            NullLogger<PluginTelemetryService>.Instance,
            // Long enough that the test's own cancellation — not a tick —
            // is what ends the loop.
            intervalOverride: TimeSpan.FromMinutes(10)
        );

        using CancellationTokenSource cts = new();
        await sut.StartAsync(cts.Token);
        await cts.CancelAsync();

        Func<Task> stop = () => sut.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
    }
}
