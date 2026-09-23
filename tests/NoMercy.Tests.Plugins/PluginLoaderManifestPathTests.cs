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
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Manifest-path scenarios that PluginBootScanTests/PluginLoaderDisposalTests/
/// PluginManagerVerificationTests do not reach: the auto-enable Initialize()
/// failure branch, the elevated-capability-plus-pre-granted-consent branch of
/// mayAutoEnable, and a manifest whose referenced assembly contains zero
/// IPlugin types.
/// </summary>
public class PluginLoaderManifestPathTests : IDisposable
{
    private static readonly Ulid ManifestFailurePluginId = Ulid.Parse("01MAN0FESTFA000RE000000000");

    private readonly string _tempPluginsDir;
    private readonly InMemoryEventBus _eventBus;

    public PluginLoaderManifestPathTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-manifest-path-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);
        _eventBus = new();
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
                Directory.Delete(_tempPluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private PluginManager BuildManager(IPluginConsentService? consentService = null) =>
        new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger<PluginManager>.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            TestStorageHelper.CreateBackend(),
            consentService: consentService
        );

    private static string GetManifestFailureBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginLoaderManifestPathTests).Assembly.Location
        )!;
        string configDir = Path.GetDirectoryName(testBinDir)!;
        string buildConfig = Path.GetFileName(configDir);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));

        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.ManifestFailure",
            "bin",
            buildConfig,
            "net10.0"
        );
    }

    private string StageManifestFailurePlugin(string manifestJson)
    {
        string binDir = GetManifestFailureBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.ManifestFailure.dll");

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"ManifestFailure plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.ManifestFailure first."
            );

        string pluginDir = Path.Combine(_tempPluginsDir, "ManifestFailure");
        Directory.CreateDirectory(pluginDir);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        File.WriteAllText(manifestPath, manifestJson);
        return manifestPath;
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_AutoEnabledInitializeThrows_MarksMalfunctionedAndPublishesError()
    {
        string manifestJson = """
            {
              "id": "01MAN0FESTFA000RE000000000",
              "name": "ManifestFailure",
              "version": "0.1.0",
              "description": "manifest auto-enable initialize failure",
              "assembly": "NoMercy.Plugin.Samples.ManifestFailure.dll",
              "autoEnabled": true
            }
            """;
        string manifestPath = StageManifestFailurePlugin(manifestJson);
        PluginManager manager = BuildManager();
        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await manager.LoadPluginFromManifestAsync(manifestPath);

        PluginInfo? info = manager
            .GetInstalledPlugins()
            .FirstOrDefault(p => p.Id == ManifestFailurePluginId);
        info.Should().NotBeNull();
        info!.Status.Should().Be(PluginStatus.Malfunctioned);
        manager
            .GetPluginInstance(ManifestFailurePluginId)
            .Should()
            .BeNull("the failed instance was disposed and never stored");
        errors.Should().ContainSingle(e => e.PluginId == ManifestFailurePluginId.ToString());

        manager.Dispose();
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_ElevatedCapabilitiesWithPreGrantedConsent_StillAutoEnables()
    {
        // mayAutoEnable = manifest.AutoEnabled && (IsBaseline || HasConsent).
        // "rest": true makes IsBaseline false — the ONLY way to reach Active
        // here is through the HasConsent(manifest.Id) side of the OR, which
        // requires a consent service that already has this exact id granted
        // BEFORE the load runs.
        string manifestJson = """
            {
              "id": "01MAN0FESTFA000RE000000000",
              "name": "ManifestFailure",
              "version": "0.1.0",
              "description": "elevated capability with pre-granted consent",
              "assembly": "NoMercy.Plugin.Samples.ManifestFailure.dll",
              "autoEnabled": true,
              "capabilities": { "rest": true }
            }
            """;
        string manifestPath = StageManifestFailurePlugin(manifestJson);
        InMemoryConsentStore consentStore = new();
        consentStore.Add(
            ManifestFailurePluginId,
            new PluginCapabilities { Rest = true },
            new Version(0, 1, 0)
        );
        PluginConsentService consentService = new(consentStore);
        PluginManager manager = BuildManager(consentService);

        await manager.LoadPluginFromManifestAsync(manifestPath);

        // ManifestAutoEnableInitializeThrowsPlugin always throws in Initialize(),
        // so mayAutoEnable=true still ends in Malfunctioned — but reaching that
        // Initialize() call AT ALL only happens when mayAutoEnable evaluated
        // true, which is exactly the branch this test targets.
        PluginInfo? info = manager
            .GetInstalledPlugins()
            .FirstOrDefault(p => p.Id == ManifestFailurePluginId);
        info.Should().NotBeNull();
        info!.Status.Should().Be(PluginStatus.Malfunctioned);

        manager.Dispose();
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_ElevatedCapabilitiesWithoutConsent_StaysDisabled()
    {
        string manifestJson = """
            {
              "id": "01MAN0FESTFA000RE000000000",
              "name": "ManifestFailure",
              "version": "0.1.0",
              "description": "elevated capability without consent",
              "assembly": "NoMercy.Plugin.Samples.ManifestFailure.dll",
              "autoEnabled": true,
              "capabilities": { "rest": true }
            }
            """;
        string manifestPath = StageManifestFailurePlugin(manifestJson);
        PluginManager manager = BuildManager(new PluginConsentService(new InMemoryConsentStore()));

        await manager.LoadPluginFromManifestAsync(manifestPath);

        // Never auto-enabled, so Initialize() (which always throws) is never
        // even called — the plugin loads Disabled, not Malfunctioned.
        PluginInfo? info = manager
            .GetInstalledPlugins()
            .FirstOrDefault(p => p.Id == ManifestFailurePluginId);
        info.Should().NotBeNull();
        info!.Status.Should().Be(PluginStatus.Disabled);

        manager.Dispose();
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_MultiTypeAssembly_NeverThrowsRegardlessOfPerTypeOutcome()
    {
        // The manifest path represents ONE logical plugin per manifest.Id — a
        // multi-type assembly here is out of contract (see
        // PluginLoaderFailureFixtureTests' class doc) and this deliberately
        // does not assert which type "wins" the registry slot. Its purpose is
        // reflection-filter coverage: `assembly.GetTypes().Where(predicate)`
        // evaluates the predicate against EVERY type in the module — including
        // AbstractPluginBase (assignable to IPlugin, but abstract) — before
        // the foreach that processes pluginTypes ever runs, regardless of
        // whether that foreach later aborts on a throwing type.
        string binDir = GetFailuresPluginBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.Failures.dll");
        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Failures plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Failures first."
            );

        string pluginDir = Path.Combine(_tempPluginsDir, "FailuresViaManifest");
        Directory.CreateDirectory(pluginDir);
        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        Ulid manifestId = Ulid.NewUlid();
        string manifestJson = $$"""
            {
              "id": "{{manifestId}}",
              "name": "FailuresViaManifest",
              "version": "0.1.0",
              "description": "multi-type assembly staged via manifest, out of contract by design",
              "assembly": "NoMercy.Plugin.Samples.Failures.dll",
              "autoEnabled": false
            }
            """;
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        File.WriteAllText(manifestPath, manifestJson);
        PluginManager manager = BuildManager();

        Func<Task> act = () => manager.LoadPluginFromManifestAsync(manifestPath);

        await act.Should().NotThrowAsync();

        manager.Dispose();
    }

    /// <summary>
    /// A dependency missing beside the assembly makes <c>Assembly.GetTypes()</c>
    /// throw <see cref="ReflectionTypeLoadException"/> for the whole module —
    /// staged deliberately without <c>Newtonsoft.Json.dll</c>, which
    /// <see cref="TypeSignatureDependsOnMissingAssemblyPlugin"/>'s own field
    /// signature needs. Before this loader wrote the resulting reason nowhere
    /// but the log, a plugin like this had no <see cref="LoadedPlugin"/> at
    /// all — the dashboard reads the registry, so it simply never appeared,
    /// rather than appearing broken with a reason.
    /// </summary>
    [Fact]
    public async Task LoadPluginFromManifestAsync_AssemblyFailsReflectionLoad_RegistersMalfunctionedWithReason()
    {
        string binDir = GetFailuresPluginBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.Failures.dll");
        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Failures plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Failures first."
            );

        string pluginDir = Path.Combine(_tempPluginsDir, "ReflectionLoadFailure");
        Directory.CreateDirectory(pluginDir);

        // Every dependency EXCEPT Newtonsoft.Json.dll, so GetTypes() throws.
        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
        {
            if (
                Path.GetFileName(file)
                    .Equals("Newtonsoft.Json.dll", StringComparison.OrdinalIgnoreCase)
            )
                continue;

            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        Ulid manifestId = Ulid.NewUlid();
        string manifestJson = $$"""
            {
              "id": "{{manifestId}}",
              "name": "ReflectionLoadFailure",
              "version": "0.1.0",
              "description": "assembly missing a dependency its own type signature needs",
              "assembly": "NoMercy.Plugin.Samples.Failures.dll",
              "autoEnabled": false
            }
            """;
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        File.WriteAllText(manifestPath, manifestJson);
        PluginManager manager = BuildManager();

        await manager.LoadPluginFromManifestAsync(manifestPath);

        PluginInfo? info = manager.GetInstalledPlugins().FirstOrDefault(p => p.Id == manifestId);
        info.Should()
            .NotBeNull("a plugin whose assembly fails to load must still be visible as broken");
        info!.Status.Should().Be(PluginStatus.Malfunctioned);
        info.Name.Should().Be("ReflectionLoadFailure");
        info.Malfunction.Should().NotBeNullOrWhiteSpace();

        manager.Dispose();
    }

    private static string GetFailuresPluginBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginLoaderManifestPathTests).Assembly.Location
        )!;
        string configDir = Path.GetDirectoryName(testBinDir)!;
        string buildConfig = Path.GetFileName(configDir);
        string repoRoot = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", ".."));

        return Path.Combine(
            repoRoot,
            "tests",
            "NoMercy.Plugin.Samples.Failures",
            "bin",
            buildConfig,
            "net10.0"
        );
    }

    [Fact]
    public async Task LoadPluginFromManifestAsync_AssemblyHasNoPluginTypes_UnloadsContextWithoutRegisteringAnything()
    {
        // NoMercy.PluginSdk.Abstractions.dll is a real, validly-loadable assembly
        // with zero concrete IPlugin implementations — a manifest can
        // (incorrectly, but not fatally) point at any real assembly.
        string pluginDir = Path.Combine(_tempPluginsDir, "NoPluginTypes");
        Directory.CreateDirectory(pluginDir);
        string abstractionsSrc = typeof(IPlugin).Assembly.Location;
        string abstractionsDest = Path.Combine(pluginDir, "NoMercy.PluginSdk.Abstractions.dll");
        File.Copy(abstractionsSrc, abstractionsDest, overwrite: true);

        Ulid manifestId = Ulid.NewUlid();
        string manifestJson = $$"""
            {
              "id": "{{manifestId}}",
              "name": "NoPluginTypes",
              "version": "0.1.0",
              "description": "assembly with zero plugin types",
              "assembly": "NoMercy.PluginSdk.Abstractions.dll",
              "autoEnabled": true
            }
            """;
        string manifestPath = Path.Combine(pluginDir, "plugin.json");
        File.WriteAllText(manifestPath, manifestJson);
        PluginManager manager = BuildManager();

        Func<Task> act = () => manager.LoadPluginFromManifestAsync(manifestPath);

        await act.Should().NotThrowAsync();
        manager.GetInstalledPlugins().Should().NotContain(p => p.Id == manifestId);

        manager.Dispose();
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
