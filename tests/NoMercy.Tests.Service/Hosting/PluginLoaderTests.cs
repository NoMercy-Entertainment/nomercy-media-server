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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Hooks;
using NoMercy.Service.Hosting;

namespace NoMercy.Tests.Service.Hosting;

/// <summary>
/// <see cref="PluginLoader"/> is the boot step that scans the plugins directory
/// and registers every plugin's cron jobs. A missing plugins directory must
/// return an empty list rather than throw (documented on <see cref="IPluginLoader.LoadPlugins"/>),
/// and cron registration must always run — even when zero plugins loaded — so a
/// plugin that only contributes a cron job without needing to "load" anything
/// still gets wired up.
/// </summary>
[Trait("Category", "Unit")]
public class PluginLoaderTests
{
    private static PluginLoadResult FakeResult(string name, string version) =>
        new(Ulid.NewUlid(), name, version, Mock.Of<IPlugin>());

    private static PluginApplicationPartRegistrar PartRegistrar() =>
        new(new(), new(), NullLogger<PluginApplicationPartRegistrar>.Instance);

    [Fact]
    public async Task LoadPlugins_ReturnsWhatPluginManagerLoads()
    {
        List<PluginLoadResult> expected = [FakeResult("Echo", "1.0.0")];
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>()
        );

        IReadOnlyList<PluginLoadResult> result = await loader.LoadPlugins(CancellationToken.None);

        result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task LoadPlugins_NoPluginsFound_ReturnsEmptyListWithoutThrowing()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>()
        );

        IReadOnlyList<PluginLoadResult> result = await loader.LoadPlugins(CancellationToken.None);

        result.Should().BeEmpty();
        cronRegistrar.Verify(r => r.RegisterAll(), Times.Once);
    }

    [Fact]
    public async Task LoadPlugins_AlwaysRegistersCronJobsAfterLoading()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager
            .Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([FakeResult("Sample", "2.0.0")]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();

        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>()
        );
        await loader.LoadPlugins(CancellationToken.None);

        cronRegistrar.Verify(r => r.RegisterAll(), Times.Once);
    }

    [Fact]
    public async Task LoadPlugins_PassesCancellationTokenThrough()
    {
        Mock<IPluginManager> pluginManager = new();
        pluginManager.Setup(m => m.LoadAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Mock<IPluginCronRegistrar> cronRegistrar = new();
        PluginLoader loader = new(
            NullLogger<PluginLoader>.Instance,
            pluginManager.Object,
            cronRegistrar.Object,
            PartRegistrar(),
            Mock.Of<IPluginRepository>()
        );
        using CancellationTokenSource cts = new();

        await loader.LoadPlugins(cts.Token);

        pluginManager.Verify(m => m.LoadAllAsync(cts.Token), Times.Once);
    }
}
