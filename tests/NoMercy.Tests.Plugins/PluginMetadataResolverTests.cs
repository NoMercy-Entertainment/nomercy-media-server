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
using NoMercy.PluginSdk.Hooks;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The metadata hook, declared since the platform existed and never called.
/// <para>
/// The merge is what these pin: whoever answers a field first keeps it, so a
/// title's metadata never depends on which plugin happened to load first, and a
/// plugin that fails leaves the rest of the answer intact.
/// </para>
/// </summary>
public class PluginMetadataResolverTests
{
    private static PluginMetadataResolver Resolver(FakeMetadataPluginManager manager) =>
        new(manager, NullLogger<PluginMetadataResolver>.Instance);

    [Fact]
    public async Task ResolveAsync_DeclaredPlugin_ReturnsWhatItKnows()
    {
        FakeMetadataPlugin plugin = new(new() { Title = "Arrival", Year = 2016 });

        MediaMetadata? answer = await Resolver(FakeMetadataPluginManager.With([(plugin, true)]))
            .ResolveAsync("Arrival", MediaType.Movie);

        answer.Should().NotBeNull();
        answer!.Year.Should().Be(2016);
    }

    [Fact]
    public async Task ResolveAsync_PluginWithoutTheDeclaredHook_IsNeverAsked()
    {
        FakeMetadataPlugin plugin = new(new() { Title = "Arrival", Year = 2016 });

        MediaMetadata? answer = await Resolver(FakeMetadataPluginManager.With([(plugin, false)]))
            .ResolveAsync("Arrival", MediaType.Movie);

        answer.Should().BeNull();
        plugin.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_SecondPlugin_FillsOnlyWhatTheFirstLeftEmpty()
    {
        FakeMetadataPlugin first = new(new() { Title = "Arrival", Year = 2016 });
        FakeMetadataPlugin second = new(
            new()
            {
                Title = "Arrival",
                Year = 1999,
                Overview = "A linguist meets a heptapod.",
            }
        );

        MediaMetadata? answer = await Resolver(
                FakeMetadataPluginManager.With([(first, true), (second, true)])
            )
            .ResolveAsync("Arrival", MediaType.Movie);

        answer!.Year.Should().Be(2016, "the first answer keeps every field it filled");
        answer.Overview.Should().Be("A linguist meets a heptapod.");
    }

    [Fact]
    public async Task ResolveAsync_PluginThatThrows_LeavesTheOtherAnswersIntact()
    {
        FakeMetadataPlugin throwing = new(null, throws: true);
        FakeMetadataPlugin good = new(new() { Title = "Arrival", Year = 2016 });

        MediaMetadata? answer = await Resolver(
                FakeMetadataPluginManager.With([(throwing, true), (good, true)])
            )
            .ResolveAsync("Arrival", MediaType.Movie);

        answer!.Year.Should().Be(2016);
    }

    [Fact]
    public async Task ResolveAsync_PluginThatHangs_IsCutOffAtTheTimeout()
    {
        FakeMetadataPlugin hanging = new(null, hangs: true);

        PluginMetadataResolver resolver = new(
            FakeMetadataPluginManager.With([(hanging, true)]),
            NullLogger<PluginMetadataResolver>.Instance
        )
        {
            PerPluginTimeout = TimeSpan.FromMilliseconds(50),
        };

        MediaMetadata? answer = await resolver.ResolveAsync("Arrival", MediaType.Movie);

        answer.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_NoPluginAnswered_IsNullSoTheProvidersValueStands()
    {
        FakeMetadataPlugin silent = new(null);

        MediaMetadata? answer = await Resolver(FakeMetadataPluginManager.With([(silent, true)]))
            .ResolveAsync("Arrival", MediaType.Movie);

        answer.Should().BeNull();
    }

    private sealed class FakeMetadataPlugin(
        MediaMetadata? answer,
        bool throws = false,
        bool hangs = false
    ) : IMetadataPlugin
    {
        public bool WasCalled { get; private set; }

        public Ulid Id { get; } = Ulid.NewUlid();
        public string Name => "Fake metadata";
        public string Description => "Fake";
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public async Task<MediaMetadata?> GetMetadataAsync(
            string title,
            MediaType type,
            CancellationToken ct = default
        )
        {
            WasCalled = true;

            if (throws)
                throw new InvalidOperationException("plugin blew up");

            if (hangs)
                await Task.Delay(Timeout.Infinite, ct);

            return answer;
        }
    }

    private sealed class FakeMetadataPluginManager : IPluginManager
    {
        private readonly List<IMetadataPlugin> _plugins = [];
        private readonly Dictionary<Ulid, PluginCapabilities?> _capabilities = [];

        public static FakeMetadataPluginManager With(
            IReadOnlyList<(IMetadataPlugin Plugin, bool DeclaresHook)> plugins
        )
        {
            FakeMetadataPluginManager manager = new();

            foreach ((IMetadataPlugin plugin, bool declaresHook) in plugins)
            {
                manager._plugins.Add(plugin);
                manager._capabilities[plugin.Id] = declaresHook
                    ? new() { Hooks = [PluginHookCapability.Metadata] }
                    : new() { Hooks = [] };
            }

            return manager;
        }

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() =>
            [
                .. _plugins.Select(plugin => new PluginInfo
                {
                    Id = plugin.Id,
                    Name = plugin.Name,
                    Description = plugin.Description,
                    Version = plugin.Version,
                    Status = PluginStatus.Active,
                    Capabilities = _capabilities[plugin.Id],
                }),
            ];

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => _plugins.OfType<T>();

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);
    }
}
