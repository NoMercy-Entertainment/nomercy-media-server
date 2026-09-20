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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Stages the NoMercy.Plugin.Samples.Failures fixture assembly — three real
/// IPlugin types (a healthy one, one whose Initialize/Dispose both throw, and
/// one whose constructor throws) plus a healthy/abstract
/// IPluginServiceRegistrator pair — and drives them through
/// <c>PluginManager.LoadPluginAssemblyAsync</c> directly. That is the ONE
/// loader entry point with true per-type failure isolation (unlike the
/// manifest path, which represents a single logical plugin per manifest.Id),
/// so a single call here exercises the loader's full success AND malfunction
/// handling for a multi-type assembly in one pass.
///
/// The fixture project is referenced with ReferenceOutputAssembly="false" (its
/// DLL must never land in the default AssemblyLoadContext — see
/// NoMercy.Tests.Plugins.csproj), so its types are never usable at compile
/// time here. The fixed plugin ids below are copied from the fixture's own
/// source (NoMercy.Plugin.Samples.Failures/*.cs) rather than referenced.
/// </summary>
public class PluginLoaderFailureFixtureTests : IDisposable
{
    private static readonly Ulid ConstructorThrowsPluginId = Ulid.Parse(
        "01SAMPLE000000000000000001"
    );
    private static readonly Ulid InitializeThrowsPluginId = Ulid.Parse(
        "01SAMPLE000000000000000002"
    );
    private static readonly Ulid ServiceRegistratorPluginId = Ulid.Parse(
        "01SAMPLE000000000000000003"
    );
    private static readonly Ulid InitializeThrowsDisposeSucceedsPluginId = Ulid.Parse(
        "01SAMPLE000000000000000004"
    );
    private static readonly Ulid ReachesARemovedMemberPluginId = Ulid.Parse(
        "01SAMPLE000000000000000005"
    );
    private static readonly Ulid StaleMemberPluginId = Ulid.Parse("01SAMPLE000000000000000006");
    private static readonly Ulid TypeSignatureDependsOnMissingAssemblyPluginId = Ulid.Parse(
        "01SAMPLE000000000000000005"
    );

    private readonly string _tempPluginsDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly PluginManager _manager;

    public PluginLoaderFailureFixtureTests()
    {
        _tempPluginsDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-loader-failures-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempPluginsDir);

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
        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_tempPluginsDir))
                Directory.Delete(_tempPluginsDir, recursive: true);
        }
        catch (Exception) { }
    }

    private static string GetFailuresPluginBinDir()
    {
        string testBinDir = Path.GetDirectoryName(
            typeof(PluginLoaderFailureFixtureTests).Assembly.Location
        )!;
        string tfmDir = testBinDir;
        string configDir = Path.GetDirectoryName(tfmDir)!;
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

    private string StageFailuresPluginDll()
    {
        string binDir = GetFailuresPluginBinDir();
        string dllSrc = Path.Combine(binDir, "NoMercy.Plugin.Samples.Failures.dll");

        if (!File.Exists(dllSrc))
            throw new FileNotFoundException(
                $"Failures plugin DLL not found at '{dllSrc}'. Build NoMercy.Plugin.Samples.Failures first."
            );

        string pluginDir = Path.Combine(_tempPluginsDir, "Failures");
        Directory.CreateDirectory(pluginDir);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        return Path.Combine(pluginDir, "NoMercy.Plugin.Samples.Failures.dll");
    }

    // NoMercy.Plugins.Abstractions.dll is a real, validly-loadable .NET
    // assembly that defines zero concrete IPlugin implementations (only
    // interfaces, enums, and DTOs) — a real assembly with no plugin types is
    // exactly the case LoadPluginAssemblyAsync's `pluginTypes.Count == 0` guard
    // exists for, with no new fixture needed.
    private static string GetAbstractionsAssemblyPath()
    {
        return typeof(IPlugin).Assembly.Location;
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_KnownPlugin_BuildsTheContextWithTheDeclaredCapabilities()
    {
        // The reload path has only the assembly — no plugin.json beside it — so
        // the capabilities have to come from what the registry already knows.
        // Built without them, the context's network allowlist is empty and every
        // host the manifest declared is denied at the first outbound request.
        string dllPath = StageFailuresPluginDll();
        PluginRegistry registry = new();
        RecordingContextFactory factory = new(
            TestPluginPlatform.ContextFactory(
                _eventBus,
                TestStorageHelper.CreateStorage(_tempPluginsDir)
            )
        );
        PluginCapabilities declared = new() { Network = new() { Hosts = ["**"] } };

        registry[ServiceRegistratorPluginId] = new(
            new()
            {
                Id = ServiceRegistratorPluginId,
                Name = "Sample",
                Description = "d",
                Version = new(1, 0, 0),
                Status = PluginStatus.Disabled,
                Capabilities = declared,
            },
            null,
            null
        );

        PluginLoader loader = new(
            _eventBus,
            new MinimalServiceProvider(),
            NullLogger.Instance,
            _tempPluginsDir,
            TestStorageHelper.CreateStorage(_tempPluginsDir),
            registry,
            new PluginVerifier(),
            new PluginConsentService(new InMemoryConsentStore()),
            factory
        );

        await loader.LoadPluginAssemblyAsync(dllPath);

        factory
            .CapabilitiesFor(ServiceRegistratorPluginId)
            .Should()
            .BeSameAs(
                declared,
                "a reload that drops the capabilities builds a context that denies every declared host"
            );
    }

    /// <summary>
    /// Passes every context build through to the real factory and remembers what
    /// each plugin's context was built with.
    /// </summary>
    private sealed class RecordingContextFactory(IPluginContextFactory inner)
        : IPluginContextFactory
    {
        private readonly Dictionary<Ulid, PluginCapabilities?> _seen = [];

        public IPluginContext Create(
            Ulid pluginId,
            string dataFolderPath,
            ILogger logger,
            PluginCapabilities? capabilities,
            string? pluginName = null,
            Version? pluginVersion = null
        )
        {
            _seen[pluginId] = capabilities;

            return inner.Create(
                pluginId,
                dataFolderPath,
                logger,
                capabilities,
                pluginName,
                pluginVersion
            );
        }

        public PluginCapabilities? CapabilitiesFor(Ulid pluginId) =>
            _seen.TryGetValue(pluginId, out PluginCapabilities? capabilities) ? capabilities : null;
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_MultiTypeAssembly_IsolatesEachTypesFailure()
    {
        string dllPath = StageFailuresPluginDll();

        await _manager.LoadPluginAssemblyAsync(dllPath);

        IReadOnlyList<PluginInfo> installed = _manager.GetInstalledPlugins();

        // ConstructorThrowsPlugin never produced an instance, so SafePluginIdentity
        // read a null instance back — Id stayed Ulid.Empty, and the loader's
        // `if (identity.Id != Ulid.Empty)` guard means it was never recorded at all.
        installed.Should().NotContain(p => p.Id == ConstructorThrowsPluginId);

        // InitializeThrowsPlugin constructed fine (a real, non-empty Id was read),
        // so its failure IS recorded — as Malfunctioned, not silently dropped.
        // Its OWN Dispose() also throws, exercising the nested disposeEx catch.
        PluginInfo? malfunctioned = installed.FirstOrDefault(p => p.Id == InitializeThrowsPluginId);
        malfunctioned.Should().NotBeNull();
        malfunctioned!.Status.Should().Be(PluginStatus.Malfunctioned);

        // Same Initialize-throws shape, but its Dispose() succeeds cleanly —
        // the complementary case to InitializeThrowsPlugin above.
        PluginInfo? malfunctionedCleanDispose = installed.FirstOrDefault(p =>
            p.Id == InitializeThrowsDisposeSucceedsPluginId
        );
        malfunctionedCleanDispose.Should().NotBeNull();
        malfunctionedCleanDispose!.Status.Should().Be(PluginStatus.Malfunctioned);

        // ServiceRegistratorPlugin is one of two healthy types — it must load
        // Active with a live instance, proving the failing types never aborted
        // the rest of the assembly's load.
        PluginInfo? healthy = installed.FirstOrDefault(p => p.Id == ServiceRegistratorPluginId);
        healthy.Should().NotBeNull();
        healthy!.Status.Should().Be(PluginStatus.Active);
        _manager.GetPluginInstance(ServiceRegistratorPluginId).Should().NotBeNull();

        // The other healthy type — its own type SIGNATURE references
        // Newtonsoft.Json (present here), unlike ServiceRegistratorPlugin which
        // only references it from inside a method body.
        PluginInfo? otherHealthy = installed.FirstOrDefault(p =>
            p.Id == TypeSignatureDependsOnMissingAssemblyPluginId
        );
        otherHealthy.Should().NotBeNull();
        otherHealthy!.Status.Should().Be(PluginStatus.Active);

        installed.Should().HaveCount(4, "ConstructorThrowsPlugin must never reach the registry");
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_AssemblyWithNoPluginTypes_UnloadsContextWithoutRegisteringAnything()
    {
        string abstractionsPath = GetAbstractionsAssemblyPath();

        Func<Task> act = () => _manager.LoadPluginAssemblyAsync(abstractionsPath);

        await act.Should().NotThrowAsync();
        _manager.GetInstalledPlugins().Should().BeEmpty();
    }

    [Fact]
    public async Task GetPluginsOfType_MixedRegistry_ReturnsOnlyMatchingActiveInstances()
    {
        string dllPath = StageFailuresPluginDll();
        await _manager.LoadPluginAssemblyAsync(dllPath);

        // Two Active instances (ServiceRegistratorPlugin,
        // TypeSignatureDependsOnMissingAssemblyPlugin) are in the registry —
        // both implement plain IPlugin, so GetPluginsOfType<IPlugin> must
        // return both, while a type NONE of them implement returns empty.
        // Proves the `is T` half of the predicate genuinely filters by type.
        IEnumerable<IPlugin> allPlugins = _manager.GetPluginsOfType<IPlugin>();
        allPlugins.Should().HaveCount(2);

        IEnumerable<IEncoderPlugin> encoderPlugins = _manager.GetPluginsOfType<IEncoderPlugin>();
        encoderPlugins.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPluginsOfType_DisabledPluginWithSurvivingInstance_IsExcluded()
    {
        // DisablePluginAsync disposes the instance but the LoadedPlugin record
        // (and its Instance reference) is immutable and stays in the registry —
        // this reproduces "type matches but status is no longer Active" without
        // needing a second fixture, isolating the `&& Info.Status == Active`
        // half of the predicate from the `is T` half.
        string dllPath = StageFailuresPluginDll();
        await _manager.LoadPluginAssemblyAsync(dllPath);
        await _manager.DisablePluginAsync(ServiceRegistratorPluginId);

        IEnumerable<IPlugin> allPlugins = _manager.GetPluginsOfType<IPlugin>();

        allPlugins
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(TypeSignatureDependsOnMissingAssemblyPluginId);
    }

    [Fact]
    public async Task DisablePluginAsync_KnownActivePlugin_TransitionsToDisabled()
    {
        string dllPath = StageFailuresPluginDll();
        await _manager.LoadPluginAssemblyAsync(dllPath);

        await _manager.DisablePluginAsync(ServiceRegistratorPluginId);

        PluginInfo? info = _manager
            .GetInstalledPlugins()
            .FirstOrDefault(p => p.Id == ServiceRegistratorPluginId);
        info.Should().NotBeNull();
        info!.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public async Task UninstallPluginAsync_KnownActivePlugin_RemovesFromRegistry()
    {
        string dllPath = StageFailuresPluginDll();
        await _manager.LoadPluginAssemblyAsync(dllPath);

        await _manager.UninstallPluginAsync(ServiceRegistratorPluginId);

        _manager.GetInstalledPlugins().Should().NotContain(p => p.Id == ServiceRegistratorPluginId);
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_MultiTypeAssembly_PublishesLoadedAndErrorEvents()
    {
        string dllPath = StageFailuresPluginDll();
        List<PluginLoadedEvent> loaded = [];
        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginLoadedEvent>(
            (evt, _) =>
            {
                loaded.Add(evt);
                return Task.CompletedTask;
            }
        );
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginAssemblyAsync(dllPath);

        loaded
            .Should()
            .Contain(e => e.PluginId == ServiceRegistratorPluginId.ToString())
            .And.Contain(e =>
                e.PluginId == TypeSignatureDependsOnMissingAssemblyPluginId.ToString()
            );
        errors
            .Should()
            .Contain(e => e.PluginId == InitializeThrowsPluginId.ToString())
            .And.Contain(e => e.PluginId == InitializeThrowsDisposeSucceedsPluginId.ToString())
            .And.Contain(e => e.PluginId == Ulid.Empty.ToString());
    }

    /// <summary>
    /// The path a server actually boots through: a plugin directory with a
    /// manifest, discovered and loaded by <c>LoadAllAsync</c>. This is the site
    /// that fired on the Proxmox box, and it is a different catch from the one
    /// the assembly-only load reaches.
    /// </summary>
    [Fact]
    public async Task LoadAllAsync_RemovedMember_ReportsTheRefusalFromTheManifestPath()
    {
        // Its own assembly on purpose. The manifest path walks every plugin
        // type in the assembly it names, so a fixture sharing one with other
        // failing plugins reports whichever fails first and pins nothing.
        string binDir = GetFailuresPluginBinDir()
            .Replace("NoMercy.Plugin.Samples.Failures", "NoMercy.Plugin.Samples.StaleMember");

        string pluginDir = Path.Combine(_tempPluginsDir, "StaleMember");
        Directory.CreateDirectory(pluginDir);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)), overwrite: true);

        await File.WriteAllTextAsync(
            Path.Combine(pluginDir, "plugin.json"),
            $$"""
            {
              "id": "{{StaleMemberPluginId}}",
              "name": "StaleMember",
              "description": "Reaches a member the contract removed",
              "version": "0.6.5",
              "assembly": "NoMercy.Plugin.Samples.StaleMember.dll",
              "autoEnabled": true
            }
            """
        );

        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadAllAsync();

        // Named by id, not just counted. A refusal reported against the wrong
        // plugin sends the owner to a plugin that is working, and an assertion
        // that only asks whether some error said "get_EventBus" cannot tell
        // the two apart.
        PluginErrorOccurredEvent reported = errors
            .Should()
            .ContainSingle(
                e => e.PluginId == StaleMemberPluginId.ToString(),
                "the manifest path is the one a server boots through"
            )
            .Which;

        reported.PluginName.Should().Be("StaleMember");
        reported.ErrorMessage.Should().Contain("get_EventBus");
        reported.ErrorMessage.Should().Contain("11.0");
        reported.ErrorMessage.Should().Contain("/nomercy-plugins/migration");
    }

    /// <summary>
    /// The loader's own initialization site, staged from a real assembly on
    /// disk rather than a hand-built exception context.
    /// <para>
    /// This is the failure a real server produced at boot. Reporting
    /// <c>Method not found: 'IPluginContext.get_EventBus()'</c> names a
    /// compiler-generated getter, not the capability to declare instead, and
    /// reads as a server fault rather than a plugin built against something
    /// older.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LoadPluginAssemblyAsync_RemovedMember_ReportsTheRefusalAndNotTheGetter()
    {
        string dllPath = StageFailuresPluginDll();
        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        await _manager.LoadPluginAssemblyAsync(dllPath);

        PluginErrorOccurredEvent reported = errors
            .Should()
            .ContainSingle(e => e.PluginId == ReachesARemovedMemberPluginId.ToString())
            .Which;

        reported.ErrorMessage.Should().Contain("get_EventBus", "the author needs the member named");
        reported
            .ErrorMessage.Should()
            .Contain("11.0", "and the version to rebuild against, which the runtime never says");
        reported.ErrorMessage.Should().Contain("/nomercy-plugins/migration");

        // The plugin beside it in the same assembly fails for its own reason and
        // must keep its own message. A blanket "rebuild against 11.0" on every
        // failure would send an author chasing a contract change that is not
        // there.
        PluginErrorOccurredEvent ordinary = errors
            .Should()
            .ContainSingle(e => e.PluginId == InitializeThrowsPluginId.ToString())
            .Which;

        ordinary.ErrorMessage.Should().Contain("initialize boom");
        ordinary.ErrorMessage.Should().NotContain("11.0");
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_HealthyRegistratorPlugin_IsDiscoverableViaGetServiceRegistrators()
    {
        string dllPath = StageFailuresPluginDll();

        await _manager.LoadPluginAssemblyAsync(dllPath);

        IEnumerable<IPluginServiceRegistrator> registrators = _manager.GetServiceRegistrators();

        registrators.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_HealthyRegistratorPlugin_RegisterPluginServices_InvokesIt()
    {
        // RegisterPluginServices' foreach body only runs when GetServiceRegistrators()
        // returns at least one ACTIVE registrator — this is the one path in this
        // suite that gets a real registrator instance through the full loader
        // pipeline into that method rather than calling RegisterServices directly.
        string dllPath = StageFailuresPluginDll();
        await _manager.LoadPluginAssemblyAsync(dllPath);
        ServiceCollection services = new();

        services.RegisterPluginServices(_manager);

        services.Should().ContainSingle();
        services[0]
            .ServiceType.FullName.Should()
            .Be("NoMercy.Plugin.Samples.Failures.FailuresPluginMarker");
    }

    [Fact]
    public async Task LoadPluginAssemblyAsync_NonExistentAssemblyPath_PublishesLoadContextErrorEvent()
    {
        // AssemblyDependencyResolver's constructor throws InvalidOperationException
        // for a path with nothing on disk — this exercises the loader's OWN
        // load-context-construction catch block (distinct from the later
        // "assembly failed to load" catches, which all require the load context
        // to have been constructed successfully first).
        string missingPath = Path.Combine(_tempPluginsDir, "totally-missing-plugin.dll");
        List<PluginErrorOccurredEvent> errors = [];
        _eventBus.Subscribe<PluginErrorOccurredEvent>(
            (evt, _) =>
            {
                errors.Add(evt);
                return Task.CompletedTask;
            }
        );

        Func<Task> act = () => _manager.LoadPluginAssemblyAsync(missingPath);

        await act.Should().NotThrowAsync();
        errors.Should().ContainSingle();
        errors[0].PluginName.Should().Be("totally-missing-plugin");
        errors[0].ErrorMessage.Should().Contain("load context");
    }

    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
