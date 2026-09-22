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
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Whether the loader does what the owner chose.
/// <para>
/// The choice was written to disk and read by nothing: a plugin set to run in
/// a process of its own was loaded into the server anyway, and the dashboard
/// said it had been saved. These pin the branch that makes the setting mean
/// something at the only moment it can, which is before an assembly is mapped
/// into this process.
/// </para>
/// </summary>
public class PluginLoaderIsolationTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("01ECH0PLUG1N0000000000000A");

    private readonly string _pluginsDir;
    private readonly InMemoryEventBus _eventBus = new();
    private readonly PluginRegistry _registry = new();

    public PluginLoaderIsolationTests()
    {
        _pluginsDir = Path.Combine(Path.GetTempPath(), "nomercy-isolation-" + Ulid.NewUlid());
        Directory.CreateDirectory(_pluginsDir);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private string StageEchoPlugin()
    {
        string testBin = Path.GetDirectoryName(
            typeof(PluginLoaderIsolationTests).Assembly.Location
        )!;
        string buildConfig = Path.GetFileName(Path.GetDirectoryName(testBin)!);
        string repoRoot = Path.GetFullPath(Path.Combine(testBin, "..", "..", "..", "..", ".."));

        string binDir = Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            buildConfig,
            "net10.0"
        );

        string pluginDir = Path.Combine(_pluginsDir, "Echo");
        Directory.CreateDirectory(pluginDir);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        string manifestPath = Path.Combine(pluginDir, "plugin.json");

        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "id": "{{PluginId}}",
              "name": "Echo",
              "version": "1.2.0",
              "description": "echoes",
              "assembly": "NoMercy.Plugin.Samples.Echo.dll",
              "autoEnabled": true
            }
            """
        );

        return manifestPath;
    }

    private PluginLoader Loader(IPluginRemoteLoader? remote) =>
        new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            _registry,
            new PluginVerifier(),
            new PluginConsentService(new InMemoryConsentStore()),
            TestPluginPlatform.ContextFactory(
                _eventBus,
                TestStorageHelper.CreateStorage(_pluginsDir)
            ),
            hostOptions: null,
            remote: remote
        );

    [Fact]
    public async Task APluginTheOwnerMovedOutIsTheOneTheRegistryHolds()
    {
        FakeRemoteLoader remote = new(PluginIsolation.OutOfProcess, answers: true);

        await Loader(remote).LoadPluginFromManifestAsync(StageEchoPlugin());

        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.Instance.Should().BeSameAs(remote.Handed);
    }

    /// <summary>
    /// A load context is an assembly mapped into this process, which is the
    /// one thing the owner asked not to happen.
    /// </summary>
    [Fact]
    public async Task NothingIsMappedIntoThisProcessForAPluginRunningElsewhere()
    {
        await Loader(new FakeRemoteLoader(PluginIsolation.OutOfProcess, answers: true))
            .LoadPluginFromManifestAsync(StageEchoPlugin());

        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.LoadContext.Should().BeNull();
    }

    /// <summary>
    /// The manifest is what the dashboard reads, and it has to survive the
    /// path that never opened the assembly.
    /// </summary>
    [Fact]
    public async Task ThePluginStillArrivesWithItsNameAndVersion()
    {
        await Loader(new FakeRemoteLoader(PluginIsolation.OutOfProcess, answers: true))
            .LoadPluginFromManifestAsync(StageEchoPlugin());

        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.Info.Name.Should().Be("Echo");
        loaded.Info.Version.Should().Be(new Version(1, 2, 0));
    }

    /// <summary>
    /// An install that cannot run a plugin elsewhere still runs it. Refusing
    /// would take a working plugin away from somebody who only changed a
    /// setting, and the dashboard already says the choice is not honored yet.
    /// </summary>
    [Fact]
    public async Task AnInstallThatCannotDoItLoadsThePluginHereInstead()
    {
        FakeRemoteLoader remote = new(PluginIsolation.OutOfProcess, answers: false);

        await Loader(remote).LoadPluginFromManifestAsync(StageEchoPlugin());

        remote.Asked.Should().Be(1);
        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.LoadContext.Should().NotBeNull("the plugin has to run somewhere");
    }

    [Fact]
    public async Task APluginLeftInProcessIsNeverHandedToTheRemoteLoader()
    {
        FakeRemoteLoader remote = new(PluginIsolation.InProcess, answers: true);

        await Loader(remote).LoadPluginFromManifestAsync(StageEchoPlugin());

        remote.Asked.Should().Be(0);
        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.LoadContext.Should().NotBeNull();
    }

    /// <summary>
    /// A server with no remote loader at all is every install today, and it
    /// must behave exactly as it did before this branch existed.
    /// </summary>
    [Fact]
    public async Task AServerThatCannotRunPluginsElsewhereIsUnchanged()
    {
        await Loader(null).LoadPluginFromManifestAsync(StageEchoPlugin());

        _registry.TryGetValue(PluginId, out LoadedPlugin? loaded).Should().BeTrue();
        loaded!.LoadContext.Should().NotBeNull();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeRemoteLoader(PluginIsolation isolation, bool answers)
        : IPluginRemoteLoader
    {
        public int Asked { get; private set; }

        public IPlugin? Handed { get; private set; }

        public PluginIsolation IsolationFor(Ulid pluginId) => isolation;

        public Task<IPlugin?> LoadAsync(
            PluginDescription description,
            CancellationToken ct = default
        )
        {
            Asked++;

            if (!answers)
                return Task.FromResult<IPlugin?>(null);

            Handed = new RemotePlugin(description, new SilentHost());
            return Task.FromResult<IPlugin?>(Handed);
        }
    }

    private sealed class SilentHost : NoMercy.PluginSdk.Ipc.IPluginHostService
    {
        public Task<NoMercy.PluginSdk.Ipc.PluginCallResponse> InitializeAsync(
            NoMercy.PluginSdk.Ipc.PluginCallRequest request,
            ProtoBuf.Grpc.CallContext context = default
        ) => Task.FromResult(NoMercy.PluginSdk.Ipc.PluginCallResponse.Value("{}"));

        public Task<NoMercy.PluginSdk.Ipc.PluginCallResponse> InvokeAsync(
            NoMercy.PluginSdk.Ipc.PluginCallRequest request,
            ProtoBuf.Grpc.CallContext context = default
        ) => Task.FromResult(NoMercy.PluginSdk.Ipc.PluginCallResponse.Value("{}"));

        public Task<NoMercy.PluginSdk.Ipc.PluginHealthSnapshot> HealthAsync(
            NoMercy.PluginSdk.Ipc.PluginCallRequest request,
            ProtoBuf.Grpc.CallContext context = default
        ) =>
            Task.FromResult(
                new NoMercy.PluginSdk.Ipc.PluginHealthSnapshot(0, TimeSpan.Zero, 0, 0, 0, 0, null)
            );

        public Task<NoMercy.PluginSdk.Ipc.PluginCallResponse> ShutdownAsync(
            NoMercy.PluginSdk.Ipc.PluginCallRequest request,
            ProtoBuf.Grpc.CallContext context = default
        ) => Task.FromResult(NoMercy.PluginSdk.Ipc.PluginCallResponse.Value("{}"));
    }
}
