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
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Plugins;

// Stages the Echo sample plugin in a temp directory and verifies that
// PluginManager.LoadAllAsync discovers, loads, and returns it.
public class PluginBootScanTests : IDisposable
{
    private readonly string _tempPluginsDir;
    private readonly string _echoPluginDir;
    private readonly PluginManager _manager;
    private readonly InMemoryEventBus _eventBus;

    public PluginBootScanTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-boot-scan-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);

        _echoPluginDir = Path.Combine(_tempPluginsDir, "Echo");
        Directory.CreateDirectory(_echoPluginDir);

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
        _manager.Dispose();

        // Force GC to collect the PluginLoadContext so Windows releases the DLL file lock.
        // AssemblyLoadContext.Unload() is asynchronous — the context is only fully unloaded
        // after all roots are released and the GC has collected it.
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
        // The Echo project's own output directory. Echo must NOT be loaded into
        // the default AssemblyLoadContext (no ProjectReference copy) — loading
        // it through PluginLoadContext requires clean isolation.
        //
        // That is also why these tests need an ordinary in-repo build: a single
        // redirected BaseOutputPath puts every project in one directory, so the
        // sample lands beside the test assembly and the isolation the tests are
        // about cannot exist.
        string testBinDir = Path.GetDirectoryName(typeof(PluginBootScanTests).Assembly.Location)!;
        // The checkout, from this file's own compile-time path rather than a
        // count of folders above the test assembly. The count was right for one
        // output layout and silently wrong for any other.
        string repoRoot = RepoPaths.Root;
        // Mirror the test's own build configuration + TFM so this works under
        // both Debug (local) and Release (CI coverage) — hardcoding "Debug"
        // makes every Echo-staging test fail when CI builds Release.
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

    private void StageEchoPlugin()
    {
        string binDir = GetEchoPluginBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.Echo.dll");
        string manifestSrc = Path.Combine(binDir, "plugin.json");

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Echo plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Echo first."
            );

        // Copy DLL and all dependencies that the Echo assembly needs at runtime.
        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        // Copy the .deps.json — AssemblyDependencyResolver reads it to resolve the
        // plugin's dependencies. Without it the resolver constructor throws and the
        // plugin silently fails to load (results come back empty). This mirrors how
        // a real plugin package ships its DLL alongside its deps manifest.
        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoPluginDir, Path.GetFileName(file)), overwrite: true);

        // Copy the plugin manifest.
        if (File.Exists(manifestSrc))
            File.Copy(manifestSrc, Path.Combine(_echoPluginDir, "plugin.json"), overwrite: true);
    }

    [Fact]
    public async Task LoadAllAsync_EmptyPluginsDir_ReturnsEmpty()
    {
        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllAsync_NonExistentPluginsDir_ReturnsEmptyWithoutThrowing()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            "no-such-dir-" + Ulid.NewUlid().ToString()
        );
        PluginManager manager = new(
            new InMemoryEventBus(),
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            missing,
            TestStorageHelper.CreateStorage(missing),
            TestStorageHelper.CreateBackend()
        );

        IReadOnlyList<PluginLoadResult> results = await manager.LoadAllAsync();

        results.Should().BeEmpty();
        manager.Dispose();
    }

    [Fact]
    public async Task LoadAllAsync_DirWithNoManifest_SkipsPlugin()
    {
        // Create a plugin dir without plugin.json — should be skipped.
        string noManifestDir = Path.Combine(_tempPluginsDir, "NoManifest");
        Directory.CreateDirectory(noManifestDir);
        await File.WriteAllTextAsync(Path.Combine(noManifestDir, "something.dll"), "garbage");

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllAsync_EchoPlugin_ReturnsOneResult()
    {
        StageEchoPlugin();

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Echo");
    }

    [Fact]
    public async Task LoadAllAsync_EchoPlugin_ResultVersionMatchesManifest()
    {
        StageEchoPlugin();

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().ContainSingle();
        results[0].Version.Should().Be("0.1.0");
    }

    [Fact]
    public async Task LoadAllAsync_EchoPlugin_InstanceIsNotNull()
    {
        StageEchoPlugin();

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().ContainSingle();
        results[0].Instance.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadAllAsync_EchoPlugin_GetProfileReturnsNonNullProfile()
    {
        StageEchoPlugin();

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        IPlugin instance = results.Should().ContainSingle().Subject.Instance;
        IEncoderPlugin encoderPlugin = instance.Should().BeAssignableTo<IEncoderPlugin>().Subject;

        EncodingProfile profile = encoderPlugin.GetProfile(new() { FilePath = "/test.mkv" })!;

        profile.Should().NotBeNull();
        profile.VideoCodec.Should().Be("h264");
        profile.AudioCodec.Should().Be("aac");
    }

    /// <summary>
    /// A reload keeps what the manifest said, because consent is decided by it.
    ///
    /// <para>
    /// Enabling a plugin whose instance was discarded reloads it from the bare
    /// assembly, and there is no plugin.json beside an assembly to read. Built
    /// from the instance alone the entry lost its capabilities, so a plugin that
    /// was waiting for approval came back looking as though it needed none —
    /// permanently. The owner was left with a plugin the server would not run
    /// and no way to consent to it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadPluginAssemblyAsync_ReloadingAScannedPlugin_KeepsWhatTheManifestDeclared()
    {
        StageEchoPlugin();

        // The sample asks for nothing, and a plugin that asks for nothing needs
        // no consent — so it could not show this defect. Its own manifest is
        // amended rather than replaced, keeping the id and assembly the scan
        // matches on.
        string manifestPath = Path.Combine(_echoPluginDir, "plugin.json");
        System.Text.Json.Nodes.JsonNode manifest = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(manifestPath)
        )!;
        manifest["capabilities"] = new System.Text.Json.Nodes.JsonObject { ["rest"] = true };
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        // Scanned first, so the entry carries what its plugin.json declared.
        await _manager.LoadAllAsync();

        PluginInfo scanned = _manager.GetInstalledPlugins().Should().ContainSingle().Subject;
        scanned
            .Capabilities.Should()
            .NotBeNull(
                "the scan reads plugin.json, so the entry starts out knowing what it asked for"
            );

        // Then reloaded the way enabling reloads it: by assembly path, with no
        // manifest anywhere near it.
        await _manager.LoadPluginAssemblyAsync(
            Path.Combine(_echoPluginDir, "NoMercy.Plugin.Samples.Echo.dll")
        );

        PluginInfo after = _manager.GetInstalledPlugins().Should().ContainSingle().Subject;
        after
            .Capabilities.Should()
            .NotBeNull("a reload that forgets the capabilities can never ask for consent again");
    }

    [Fact]
    public async Task LoadAllAsync_DisabledPluginWithSurvivingInstance_IsExcludedFromResults()
    {
        // DisablePluginAsync disposes the instance but LoadedPlugin (and its
        // Instance reference) is immutable — the record stays in the registry
        // with a non-null Instance and a non-Active status. LoadAllAsync's own
        // `Instance is not null && Status == Active` filter must still exclude
        // it, isolating the `&& Status == Active` half of that guard.
        //
        // Loaded directly via LoadPluginAssemblyAsync (not staged under
        // _tempPluginsDir) so the LoadAllAsync call below's own directory
        // rescan does not reprocess and re-enable it.
        string echoBinDir = GetEchoPluginBinDir();
        string echoDllPath = Path.Combine(echoBinDir, "NoMercy.Plugin.Samples.Echo.dll");
        await _manager.LoadPluginAssemblyAsync(echoDllPath);
        PluginInfo activeBefore = _manager.GetInstalledPlugins().Should().ContainSingle().Subject;
        await _manager.DisablePluginAsync(activeBefore.Id);

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllAsync_NonAutoEnabledPluginAlongsideActiveOne_OnlyReturnsTheActiveOne()
    {
        // A manifest-driven plugin with autoEnabled:false loads Disabled with
        // Instance == null — the OTHER half of LoadAllAsync's filter (distinct
        // from the "disposed-but-still-referenced" case above, which has a
        // non-null Instance). Both plugins share the Echo assembly but use
        // different manifest ids so they occupy separate registry entries.
        StageEchoPlugin();
        string disabledDir = Path.Combine(_tempPluginsDir, "EchoDisabled");
        Directory.CreateDirectory(disabledDir);
        string binDir = GetEchoPluginBinDir();
        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(disabledDir, Path.GetFileName(file)), overwrite: true);
        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(disabledDir, Path.GetFileName(file)), overwrite: true);
        File.WriteAllText(
            Path.Combine(disabledDir, "plugin.json"),
            """
            {
              "id": "36CSK6C48H48H36CT48H248H24",
              "name": "EchoDisabled",
              "version": "0.1.0",
              "description": "same assembly, never auto-enabled",
              "assembly": "NoMercy.Plugin.Samples.Echo.dll",
              "autoEnabled": false
            }
            """
        );

        IReadOnlyList<PluginLoadResult> results = await _manager.LoadAllAsync();

        results.Should().ContainSingle();
        results[0].Name.Should().Be("Echo");
        _manager
            .GetInstalledPlugins()
            .Should()
            .Contain(p => p.Name == "EchoDisabled" && p.Status == PluginStatus.Disabled);
    }

    [Fact]
    public async Task LoadAllAsync_MalformedManifest_SkipsAndContinues()
    {
        // Bad manifest — should not throw; other plugins (if any) continue loading.
        string badDir = Path.Combine(_tempPluginsDir, "Bad");
        Directory.CreateDirectory(badDir);
        await File.WriteAllTextAsync(Path.Combine(badDir, "plugin.json"), "not json");

        Func<Task> act = () => _manager.LoadAllAsync();

        await act.Should().NotThrowAsync();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
