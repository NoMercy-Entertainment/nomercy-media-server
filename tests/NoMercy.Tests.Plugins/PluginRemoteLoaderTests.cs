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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The piece that turns the owner's isolation setting into a running process.
/// <para>
/// Everything beneath it existed already and nothing joined it up, so the
/// setting was saved and ignored.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginRemoteLoaderTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private static PluginDescription Described() =>
        new(PluginId, "Echo", "echoes", new Version(1, 0));

    private static PluginRemoteLoader Loader(
        IPluginProcessLauncher launcher,
        string? hostPath,
        PluginRuntimeMode? mode = null
    ) =>
        new(
            new PluginProcessSupervisor(launcher),
            new FakeHostExecutable(hostPath),
            NullLogger.Instance,
            () => mode ?? new PluginRuntimeMode(PluginIsolation.OutOfProcess)
        );

    [Fact]
    public void TheIsolationIsReadFromTheOwnersOwnSetting()
    {
        PluginRemoteLoader loader = Loader(
            new FakeLauncher(),
            "host.exe",
            new PluginRuntimeMode(
                PluginIsolation.InProcess,
                new Dictionary<string, PluginIsolation>
                {
                    [PluginId.ToString()] = PluginIsolation.OutOfProcess,
                }
            )
        );

        loader.IsolationFor(PluginId).Should().Be(PluginIsolation.OutOfProcess);
        loader.IsolationFor(Ulid.NewUlid()).Should().Be(PluginIsolation.InProcess);
    }

    /// <summary>
    /// An install with no plugin host beside the server cannot start one, and
    /// says so by answering nothing rather than by throwing. The loader's
    /// answer to nothing is to run the plugin here, which is a working plugin
    /// rather than none.
    /// </summary>
    [Fact]
    public async Task AnInstallWithNoPluginHostAnswersNothingAndStartsNothing()
    {
        FakeLauncher launcher = new();

        IPlugin? plugin = await Loader(launcher, hostPath: null).LoadAsync(Described());

        plugin.Should().BeNull();
        launcher.Launches.Should().Be(0);
    }

    [Fact]
    public async Task AProcessThatWouldNotStartAnswersNothing()
    {
        IPlugin? plugin = await Loader(new FailingLauncher(), "host.exe").LoadAsync(Described());

        plugin.Should().BeNull();
    }

    [Fact]
    public async Task AStartedPluginComesBackAsSomethingTheRegistryCanHold()
    {
        FakeLauncher launcher = new();

        IPlugin? plugin = await Loader(launcher, "host.exe").LoadAsync(Described());

        plugin.Should().BeOfType<RemotePlugin>();
        plugin!.Name.Should().Be("Echo");
        launcher.Launches.Should().Be(1);
    }

    private sealed class FakeHostExecutable(string? path) : IPluginHostExecutable
    {
        public string? Path => path;
    }

    private sealed class FakeLauncher : IPluginProcessLauncher
    {
        public int Launches { get; private set; }

        public Task<IPluginHostProcess> LaunchAsync(Ulid pluginId, CancellationToken ct = default)
        {
            Launches++;
            return Task.FromResult<IPluginHostProcess>(new FakeProcess(pluginId));
        }
    }

    private sealed class FailingLauncher : IPluginProcessLauncher
    {
        public Task<IPluginHostProcess> LaunchAsync(
            Ulid pluginId,
            CancellationToken ct = default
        ) => throw new InvalidOperationException("nothing started");
    }

    private sealed class FakeProcess(Ulid pluginId) : IPluginHostProcess
    {
        public Ulid PluginId => pluginId;

        public bool IsRunning => true;

        public string Token => "3F2A9C01B7E84D6A";

        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
