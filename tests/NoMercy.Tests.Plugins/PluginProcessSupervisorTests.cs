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
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Moving a plugin out of the server process is only worth anything if the
/// supervisor keeps one plugin's failure to that plugin.
/// </summary>
[Trait("Category", "Unit")]
public class PluginProcessSupervisorTests
{
    [Fact]
    public async Task ACrashedPlugin_IsStartedAgain()
    {
        CountingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher);
        Ulid plugin = Ulid.NewUlid();

        await supervisor.StartAsync(plugin);
        await supervisor.OnExitedAsync(plugin, PluginRestartCause.Crashed);

        launcher.Launches.Should().Be(2);
        supervisor.StateOf(plugin).Running.Should().BeTrue();
    }

    /// <summary>
    /// A plugin that crashes on a line it runs at startup crashes again the
    /// instant it is restarted. Without a ceiling the server spends the rest
    /// of its life spawning it.
    /// </summary>
    [Fact]
    public async Task APluginThatKeepsCrashing_IsGivenUpOnRatherThanRespawnedForever()
    {
        CountingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher, new PluginRestartLedger(limit: 3));
        Ulid plugin = Ulid.NewUlid();

        await supervisor.StartAsync(plugin);

        for (int crash = 0; crash < 10; crash++)
            await supervisor.OnExitedAsync(plugin, PluginRestartCause.Crashed);

        supervisor.StateOf(plugin).GaveUp.Should().BeTrue();
        supervisor.StateOf(plugin).Running.Should().BeFalse();
        launcher.Launches.Should().Be(3);
    }

    /// <summary>The whole point of the boundary.</summary>
    [Fact]
    public async Task OnePluginCrashingOutOfItsLives_LeavesTheOthersRunning()
    {
        CountingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher, new PluginRestartLedger(limit: 2));
        Ulid broken = Ulid.NewUlid();
        Ulid healthy = Ulid.NewUlid();

        await supervisor.StartAsync(broken);
        await supervisor.StartAsync(healthy);

        for (int crash = 0; crash < 5; crash++)
            await supervisor.OnExitedAsync(broken, PluginRestartCause.Crashed);

        supervisor.StateOf(broken).GaveUp.Should().BeTrue();
        supervisor.StateOf(healthy).Running.Should().BeTrue();
        supervisor.Running.Should().Contain(healthy);
    }

    /// <summary>
    /// A launch that throws is this plugin's failure. Letting it travel up
    /// would take down the loop starting every other plugin at boot.
    /// </summary>
    [Fact]
    public async Task ALaunchThatThrows_DoesNotStopTheOtherPluginsStarting()
    {
        ThrowingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher);
        Ulid broken = launcher.Broken;
        Ulid healthy = Ulid.NewUlid();

        bool brokenStarted = await supervisor.StartAsync(broken);
        bool healthyStarted = await supervisor.StartAsync(healthy);

        brokenStarted.Should().BeFalse();
        healthyStarted.Should().BeTrue();
        supervisor.StateOf(healthy).Running.Should().BeTrue();
    }

    /// <summary>
    /// A deliberate restart is not a crash. Counting it would let three
    /// updates in an afternoon retire a plugin that never failed.
    /// </summary>
    [Fact]
    public async Task ARequestedRestart_NeverCountsAgainstThePlugin()
    {
        CountingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher, new PluginRestartLedger(limit: 2));
        Ulid plugin = Ulid.NewUlid();

        await supervisor.StartAsync(plugin);

        for (int restart = 0; restart < 10; restart++)
            await supervisor.OnExitedAsync(plugin, PluginRestartCause.Updated);

        supervisor.StateOf(plugin).GaveUp.Should().BeFalse();
        supervisor.StateOf(plugin).Running.Should().BeTrue();
    }

    /// <summary>Asking for it explicitly is the way back.</summary>
    [Fact]
    public async Task StartingAGivenUpPluginAgain_ClearsItsRecord()
    {
        CountingLauncher launcher = new();
        PluginProcessSupervisor supervisor = new(launcher, new PluginRestartLedger(limit: 1));
        Ulid plugin = Ulid.NewUlid();

        await supervisor.StartAsync(plugin);
        await supervisor.OnExitedAsync(plugin, PluginRestartCause.Crashed);
        supervisor.StateOf(plugin).GaveUp.Should().BeTrue();

        await supervisor.StartAsync(plugin);

        supervisor.StateOf(plugin).GaveUp.Should().BeFalse();
        supervisor.StateOf(plugin).Running.Should().BeTrue();
    }
}

internal sealed class CountingLauncher : IPluginProcessLauncher
{
    public int Launches { get; private set; }

    public Task<IPluginProcess> LaunchAsync(Ulid pluginId, CancellationToken ct = default)
    {
        Launches++;
        return Task.FromResult<IPluginProcess>(new FakeProcess(pluginId));
    }
}

internal sealed class ThrowingLauncher : IPluginProcessLauncher
{
    public Ulid Broken { get; } = Ulid.NewUlid();

    public Task<IPluginProcess> LaunchAsync(Ulid pluginId, CancellationToken ct = default) =>
        pluginId == Broken
            ? throw new InvalidOperationException("the assembly is not there")
            : Task.FromResult<IPluginProcess>(new FakeProcess(pluginId));
}

internal sealed class FakeProcess(Ulid pluginId) : IPluginProcess
{
    public Ulid PluginId => pluginId;

    public bool IsRunning => true;

    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
}
