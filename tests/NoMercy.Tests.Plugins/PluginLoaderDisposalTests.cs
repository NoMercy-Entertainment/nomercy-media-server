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
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Stages the Echo sample plugin with a manifest that is NOT auto-enabled and
/// verifies PluginLoader disposes the constructed-but-never-stored instance
/// instead of discarding it unreferenced. Before the fix, a disabled plugin's
/// instance was constructed (running its constructor) but neither
/// Initialize()'d nor stored, and nothing ever called Dispose() on it.
/// </summary>
public class PluginLoaderDisposalTests : IDisposable
{
    private const string DisposalMarkerEnvVar = "NOMERCY_TEST_ECHO_DISPOSAL_MARKER";

    private readonly string _tempPluginsDir;
    private readonly string _echoPluginDir;
    private readonly string _markerPath;
    private readonly PluginManager _manager;
    private readonly InMemoryEventBus _eventBus;

    public PluginLoaderDisposalTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-loader-disposal-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);

        _echoPluginDir = Path.Combine(_tempPluginsDir, "Echo");
        Directory.CreateDirectory(_echoPluginDir);

        _markerPath = Path.Combine(_tempPluginsDir, "disposed.marker");

        _eventBus = new();
        _manager = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend()
        );
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DisposalMarkerEnvVar, null);

        _manager.Dispose();

        // Force GC to collect the PluginLoadContext so Windows releases the DLL file lock.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
                Directory.Delete(_tempPluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private static string GetEchoPluginBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginLoaderDisposalTests).Assembly.Location
        )!;
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));
        // Mirror the test's own build configuration + TFM so this works under
        // both Debug (local) and Release (CI coverage).
        string tfm = Path.GetFileName(testBinDir);
        string configuration = Path.GetFileName(Path.GetDirectoryName(testBinDir)!);
        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Echo",
            "bin",
            configuration,
            tfm
        );
    }

    private void StageEchoPlugin(bool autoEnabled)
    {
        string binDir = GetEchoPluginBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.Echo.dll");

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Echo plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Echo first."
            );

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        // A custom manifest (not the repo's committed plugin.json) so this
        // test controls autoEnabled independently of every other Echo-staging
        // test in this project.
        string manifestJson = $$"""
            {
              "id": "01ECH000000000000000000000",
              "name": "Echo",
              "version": "0.1.0",
              "description": "Sample plugin for the test suite",
              "assembly": "NoMercy.Plugin.Samples.Echo.dll",
              "autoEnabled": {{(autoEnabled ? "true" : "false")}}
            }
            """;
        File.WriteAllText(Path.Combine(_echoPluginDir, "plugin.json"), manifestJson);
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_NonAutoEnabledPlugin_DisposesConstructedInstance()
    {
        Environment.SetEnvironmentVariable(DisposalMarkerEnvVar, _markerPath);
        StageEchoPlugin(autoEnabled: false);

        await _manager.LoadPluginsFromDirectoryAsync();

        IReadOnlyList<PluginInfo> installed = _manager.GetInstalledPlugins();
        installed.Should().ContainSingle();
        installed[0].Status.Should().Be(PluginStatus.Disabled);

        // Prior to the fix, the constructed-but-never-stored instance was
        // simply discarded — Dispose() never ran and this marker never
        // appeared.
        File.Exists(_markerPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadPluginsFromDirectoryAsync_AutoEnabledPlugin_DoesNotDisposeActiveInstance()
    {
        Environment.SetEnvironmentVariable(DisposalMarkerEnvVar, _markerPath);
        StageEchoPlugin(autoEnabled: true);

        await _manager.LoadPluginsFromDirectoryAsync();

        IReadOnlyList<PluginInfo> installed = _manager.GetInstalledPlugins();
        installed.Should().ContainSingle();
        installed[0].Status.Should().Be(PluginStatus.Active);

        // An active plugin's instance is stored and stays live — the fix
        // must only dispose the instance when it is NOT going to be stored.
        File.Exists(_markerPath).Should().BeFalse();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
