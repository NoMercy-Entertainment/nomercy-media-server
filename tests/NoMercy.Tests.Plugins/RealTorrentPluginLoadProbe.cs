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
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Runs a real installed plugin through the whole load path the server uses,
/// and prints what stops it.
/// <para>
/// A probe rather than an assertion: it reports when the plugin is not on this
/// machine instead of failing, so it costs nothing in the suite. Point
/// <c>NOMERCY_PLUGINS_DIR</c> at a folder holding plugin directories copied
/// off a server.
/// </para>
/// </summary>
public class RealTorrentPluginLoadProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task Say_what_stops_it_loading()
    {
        string? pluginsPath = Environment.GetEnvironmentVariable("NOMERCY_PLUGINS_DIR");

        if (pluginsPath is null || !Directory.Exists(pluginsPath))
        {
            output.WriteLine("no plugins on this machine");
            return;
        }

        InMemoryEventBus bus = new();
        List<PluginErrorOccurredEvent> failures = [];

        bus.Subscribe<PluginErrorOccurredEvent>(
            (failure, _) =>
            {
                failures.Add(failure);
                return Task.CompletedTask;
            }
        );

        using PluginManager manager = new(
            bus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            pluginsPath,
            TestStorageHelper.CreateStorage(pluginsPath),
            TestStorageHelper.CreateBackend()
        );

        foreach (string folder in Directory.GetDirectories(pluginsPath))
            output.WriteLine(
                $"on disk: {Path.GetFileName(folder)} manifest={File.Exists(Path.Combine(folder, "plugin.json"))}"
            );

        IReadOnlyList<PluginLoadResult> loaded = await manager.LoadAllAsync();

        output.WriteLine($"installed after scan: {manager.GetInstalledPlugins().Count}");

        foreach (PluginInfo info in manager.GetInstalledPlugins())
            output.WriteLine($"  {info.Name} {info.Version} status={info.Status}");

        foreach (PluginLoadResult result in loaded)
            output.WriteLine($"LOADED {result.Name} {result.Version}");

        foreach (PluginErrorOccurredEvent failure in failures)
            output.WriteLine($"FAILED {failure.Why}");

        if (failures.Count == 0 && loaded.Count == 0)
            output.WriteLine("nothing was found to load");
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
