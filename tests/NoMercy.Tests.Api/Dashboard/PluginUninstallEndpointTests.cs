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
using Microsoft.AspNetCore.Http;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Purging a plugin's data, consent, grants and secrets cannot be irreversible
/// and silent. A client that sends no answer gets the answer the route has
/// always given: the data stays. Purging is the caller asking for it.
/// </summary>
public class PluginUninstallEndpointTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private class RecordingPluginManager : IPluginManager
    {
        public bool? KeepDataAskedFor { get; private set; }

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [];

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default)
        {
            KeepDataAskedFor = false;
            return Task.CompletedTask;
        }

        public Task UninstallPluginAsync(
            Ulid pluginId,
            bool keepData,
            CancellationToken ct = default
        )
        {
            KeepDataAskedFor = keepData;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    private static PluginController BuildController(IPluginManager pluginManager) =>
        new(
            pluginManager,
            Mock.Of<IPluginConsentService>(),
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>()
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task Uninstalling_without_an_answer_keeps_the_data_folder()
    {
        RecordingPluginManager manager = new();

        await BuildController(manager).Uninstall(PluginId);

        manager
            .KeepDataAskedFor.Should()
            .BeTrue("a client that predates the flag never asked for a purge");
    }

    [Fact]
    public async Task An_owner_who_asks_for_a_purge_gets_one()
    {
        RecordingPluginManager manager = new();

        await BuildController(manager).Uninstall(PluginId, keepData: false);

        manager.KeepDataAskedFor.Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_who_asks_to_keep_data_keeps_it()
    {
        RecordingPluginManager manager = new();

        await BuildController(manager).Uninstall(PluginId, keepData: true);

        manager.KeepDataAskedFor.Should().BeTrue();
    }
}
