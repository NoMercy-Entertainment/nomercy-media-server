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
using MediaFolderExtend = NoMercy.NmSystem.Dto.MediaFolderExtend;
using PluginMediaFile = NoMercy.PluginSdk.Abstractions.MediaFile;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The media-source hook, which was declared for the whole life of the plugin
/// platform and never called once.
/// <para>
/// Asserted by what comes back — a file the scan can go on to parse — rather
/// than by "the plugin ran", and by what does NOT: a plugin that never declared
/// the hook, and one that hangs, both contribute nothing while the scan carries
/// on. A library scan that a plugin can stop is worse than one it cannot join.
/// </para>
/// </summary>
public class PluginMediaSourceProviderTests
{
    private static PluginMediaSourceProvider Provider(FakeMediaSourcePluginManager manager) =>
        new(manager, NullLogger<PluginMediaSourceProvider>.Instance);

    [Fact]
    public async Task ScanAsync_DeclaredPlugin_ContributesItsFilesToTheScan()
    {
        FakeMediaSourcePlugin plugin = new([
            new PluginMediaFile
            {
                Path = "/library/movies/Arrival (2016)/Arrival.mkv",
                FileName = "Arrival.mkv",
                Size = 1234,
            },
        ]);

        IReadOnlyList<MediaFolderExtend> found = await Provider(
                FakeMediaSourcePluginManager.With(plugin, declaresHook: true)
            )
            .ScanAsync("/library/movies");

        found.Should().ContainSingle();
        found[0]
            .Files.Should()
            .ContainSingle(file =>
                file.Path == "/library/movies/Arrival (2016)/Arrival.mkv"
                && file.Name == "Arrival.mkv"
                && file.Extension == ".mkv"
                && file.Size == 1234
            );
    }

    /// <summary>
    /// The scan fills these from the file itself. A dispatcher that guessed them
    /// would be deciding what a plugin's file means, which is the one thing a
    /// plugin must not be able to do.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ContributedFile_IsLeftUnparsedForTheScannersOwnParser()
    {
        FakeMediaSourcePlugin plugin = new([
            new PluginMediaFile { Path = "/library/movies/x.mkv", FileName = "x.mkv" },
        ]);

        IReadOnlyList<MediaFolderExtend> found = await Provider(
                FakeMediaSourcePluginManager.With(plugin, declaresHook: true)
            )
            .ScanAsync("/library/movies");

        found[0].Files!.Single().Parsed.Should().BeNull();
        found[0].Files!.Single().FFprobe.Should().BeNull();
        found[0].Files!.Single().TagFile.Should().BeNull();
    }

    [Fact]
    public async Task ScanAsync_PluginWithoutTheDeclaredHook_ContributesNothing()
    {
        FakeMediaSourcePlugin plugin = new([
            new PluginMediaFile { Path = "/library/movies/x.mkv", FileName = "x.mkv" },
        ]);

        IReadOnlyList<MediaFolderExtend> found = await Provider(
                FakeMediaSourcePluginManager.With(plugin, declaresHook: false)
            )
            .ScanAsync("/library/movies");

        found.Should().BeEmpty();
        plugin.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ScanAsync_PluginThatThrows_IsSkippedAndTheScanContinues()
    {
        FakeMediaSourcePlugin throwing = new([], throws: true);

        IReadOnlyList<MediaFolderExtend> found = await Provider(
                FakeMediaSourcePluginManager.With(throwing, declaresHook: true)
            )
            .ScanAsync("/library/movies");

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanAsync_PluginThatHangs_IsCutOffAtTheTimeout()
    {
        FakeMediaSourcePlugin hanging = new([], hangs: true);

        PluginMediaSourceProvider provider = new(
            FakeMediaSourcePluginManager.With(hanging, declaresHook: true),
            NullLogger<PluginMediaSourceProvider>.Instance
        )
        {
            PerPluginTimeout = TimeSpan.FromMilliseconds(50),
        };

        IReadOnlyList<MediaFolderExtend> found = await provider.ScanAsync("/library/movies");

        found.Should().BeEmpty();
    }

    [Fact]
    public async Task ScanAsync_PluginThatFoundNothing_AddsNoEmptyFolder()
    {
        FakeMediaSourcePlugin empty = new([]);

        IReadOnlyList<MediaFolderExtend> found = await Provider(
                FakeMediaSourcePluginManager.With(empty, declaresHook: true)
            )
            .ScanAsync("/library/movies");

        found.Should().BeEmpty();
    }

    private sealed class FakeMediaSourcePlugin(
        IReadOnlyList<PluginMediaFile> files,
        bool throws = false,
        bool hangs = false
    ) : IMediaSourcePlugin
    {
        public bool WasCalled { get; private set; }

        public Ulid Id { get; } = Ulid.NewUlid();
        public string Name => "Fake media source";
        public string Description => "Fake";
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public async Task<IEnumerable<PluginMediaFile>> ScanAsync(
            string path,
            CancellationToken ct = default
        )
        {
            WasCalled = true;

            if (throws)
                throw new InvalidOperationException("plugin blew up");

            if (hangs)
                await Task.Delay(Timeout.Infinite, ct);

            return files;
        }
    }

    private sealed class FakeMediaSourcePluginManager : IPluginManager
    {
        private readonly List<IMediaSourcePlugin> _plugins = [];
        private readonly Dictionary<Ulid, PluginCapabilities?> _capabilities = [];

        public static FakeMediaSourcePluginManager With(
            IMediaSourcePlugin plugin,
            bool declaresHook
        )
        {
            FakeMediaSourcePluginManager manager = new();
            manager._plugins.Add(plugin);
            manager._capabilities[plugin.Id] = declaresHook
                ? new() { Hooks = [PluginHookCapability.MediaSource] }
                : new() { Hooks = [] };
            return manager;
        }

        public IReadOnlyList<PluginInfo> GetInstalledPlugins() =>
            _plugins
                .Select(plugin => new PluginInfo
                {
                    Id = plugin.Id,
                    Name = plugin.Name,
                    Description = plugin.Description,
                    Version = plugin.Version,
                    Status = PluginStatus.Active,
                    Capabilities = _capabilities[plugin.Id],
                })
                .ToList();

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
