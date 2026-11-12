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
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A revoked build is refused before its assembly is loaded, in both load
/// paths and in every trust mode, and the owner cannot enable it (AUD-1134).
/// The Echo sample marks its type setup and its constructor on disk, so a
/// test sees whether either ran.
/// </summary>
[Collection(EchoConstructionMarkerCollection.Name)]
public class PluginRevokedBuildNeverLoadsTests : IDisposable
{
    private const string ConstructionRootEnvVar = "NOMERCY_TEST_ECHO_CONSTRUCTION_ROOT";
    private const string EchoDll = "NoMercy.Plugin.Samples.Echo.dll";

    private static readonly Ulid Echo = Ulid.Parse("01ECH000000000000000000000");
    private static readonly Ulid Other = Ulid.Parse("01HZX5D3K8QV7W2N4M6P9R0T1B");
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _pluginsDir = Path.Combine(
        Path.GetTempPath(),
        "nomercy-revoked-build-" + Ulid.NewUlid()
    );
    private readonly string _echoDir;
    private readonly InMemoryEventBus _eventBus = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginConsentService _consent = new(new InMemoryConsentStore());

    public PluginRevokedBuildNeverLoadsTests()
    {
        _echoDir = Path.Combine(_pluginsDir, "Echo");
        Directory.CreateDirectory(_echoDir);
        Environment.SetEnvironmentVariable(ConstructionRootEnvVar, _pluginsDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConstructionRootEnvVar, null);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_manifest_load_of_a_revoked_build_never_constructs_the_plugin(bool enforce)
    {
        StageEcho();
        PluginRunGate gate = Gate(revoked: Echo, enforce);

        await Loader(gate).LoadPluginFromManifestAsync(Path.Combine(_echoDir, "plugin.json"));

        EchoRan().Should().BeFalse("a revoked build's code does not run (AUD-1134)");
        LoadedPlugin listed = Registered();
        listed.Info.Status.Should().Be(PluginStatus.Disabled);
        listed.Instance.Should().BeNull();
        listed.LoadContext.Should().BeNull("the assembly of a revoked build is not loaded");
        gate.LastRefusal(Echo)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_assembly_load_of_a_revoked_build_never_constructs_the_plugin(bool enforce)
    {
        StageEcho();
        _registry[Echo] = Listed(PluginStatus.Disabled);
        PluginRunGate gate = Gate(revoked: Echo, enforce);

        await Loader(gate).LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll));

        EchoRan().Should().BeFalse("a revoked build's code does not run (AUD-1134)");
        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
        gate.LastRefusal(Echo)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_owner_enable_of_a_revoked_build_throws_and_the_plugin_stays_off(
        bool enforce
    )
    {
        StageEcho();
        _registry[Echo] = Listed(PluginStatus.Disabled);
        PluginRunGate gate = Gate(revoked: Echo, enforce);

        Func<Task> enable = () => Lifecycle(gate).EnablePluginAsync(Echo);

        (await enable.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*revoked*");
        EchoRan().Should().BeFalse("a revoked build's code does not run (AUD-1134)");
        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_owner_enable_of_a_revoked_build_that_kept_its_instance_does_not_start_it(
        bool enforce
    )
    {
        StageEcho();
        RecordingPlugin instance = new(Echo);
        _registry[Echo] = Listed(PluginStatus.Disabled, instance);

        Func<Task> enable = () => Lifecycle(Gate(revoked: Echo, enforce)).EnablePluginAsync(Echo);

        (await enable.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*revoked*");
        instance.Initialized.Should().BeFalse();
        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
    }

    [Fact]
    public async Task A_build_refused_for_consent_still_loads_and_waits_as_before()
    {
        StageEcho(capabilities: """{ "rest": true }""");
        PluginRunGate gate = Gate(revoked: Other, enforce: true);

        await Loader(gate).LoadPluginFromManifestAsync(Path.Combine(_echoDir, "plugin.json"));

        EchoRan().Should().BeTrue("only a revocation stops the load");
        LoadedPlugin listed = Registered();
        listed.Info.Status.Should().Be(PluginStatus.Disabled);
        listed.Instance.Should().BeNull();
        listed.LoadContext.Should().NotBeNull();
        gate.LastRefusal(Echo)!.Code.Should().Be(PluginRefusalCodes.CapabilityNotConsented);
    }

    [Fact]
    public async Task A_healthy_build_loads_from_its_manifest_as_before()
    {
        StageEcho();
        PluginRunGate gate = Gate(revoked: Other, enforce: true);

        await Loader(gate).LoadPluginFromManifestAsync(Path.Combine(_echoDir, "plugin.json"));

        EchoRan().Should().BeTrue();
        Registered().Info.Status.Should().Be(PluginStatus.Active);
        Registered().Instance.Should().NotBeNull();
        gate.LastRefusal(Echo).Should().BeNull();
    }

    [Fact]
    public async Task A_healthy_build_enables_as_before()
    {
        StageEcho();
        _registry[Echo] = Listed(PluginStatus.Disabled);

        await Lifecycle(Gate(revoked: Other, enforce: true)).EnablePluginAsync(Echo);

        EchoRan().Should().BeTrue();
        Registered().Info.Status.Should().Be(PluginStatus.Active);
        Registered().Instance.Should().NotBeNull();
    }

    private bool EchoRan() =>
        File.Exists(Path.Combine(_pluginsDir, "echo-type-initialized.marker"))
        || File.Exists(Path.Combine(_pluginsDir, "echo-constructed.marker"));

    private LoadedPlugin Registered()
    {
        _registry.TryGetValue(Echo, out LoadedPlugin? loaded).Should().BeTrue();
        return loaded!;
    }

    private LoadedPlugin Listed(PluginStatus status, IPlugin? instance = null) =>
        new(
            new()
            {
                Id = Echo,
                Name = "Echo",
                Description = "d",
                Version = new(0, 1, 0),
                Status = status,
                AssemblyPath = Path.Combine(_echoDir, EchoDll),
                ManifestPath = Path.Combine(_echoDir, "plugin.json"),
            },
            instance,
            null
        );

    /// <summary>The real run gate: the revocation check first, then consent.</summary>
    private PluginRunGate Gate(Ulid revoked, bool enforce)
    {
        StubRevocationStore store = new(
            new(Now, [new PluginRevocationEntry(revoked, null, null, "plugins.revoked.security")])
        );

        return new([
            new PluginRevocationRunCheck(
                new(store, new StubClock(Now), new(enforce, NullLogger.Instance)),
                store
            ),
            new PluginConsentRunCheck(_consent),
        ]);
    }

    private PluginLoader Loader(IPluginRunGate gate) =>
        new(
            _eventBus,
            new GateServiceProvider(gate),
            NullLogger.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            _registry,
            new PluginVerifier(),
            _consent,
            TestPluginPlatform.ContextFactory(
                _eventBus,
                TestStorageHelper.CreateStorage(_pluginsDir)
            )
        );

    private PluginLifecycleManager Lifecycle(IPluginRunGate gate)
    {
        IStorage storage = TestStorageHelper.CreateStorage(_pluginsDir);
        IPluginConfiguration platform = new PluginConfiguration(
            Path.Combine(_pluginsDir, "data", "platform"),
            storage
        );

        return new(
            _eventBus,
            new GateServiceProvider(gate),
            NullLogger.Instance,
            _pluginsDir,
            storage,
            _registry,
            Loader(gate),
            TestPluginPlatform.ContextFactory(_eventBus, storage),
            new PluginDataPurge(
                _pluginsDir,
                storage,
                new PluginConsentService(new ConfigPluginConsentStore(platform)),
                new ConfigPluginGrantStore(platform),
                platform
            ),
            _consent
        );
    }

    private void StageEcho(string? capabilities = null)
    {
        string binDir = Path.GetDirectoryName(
            CodeScanVerificationStageTests.SampleDllPath("NoMercy.Plugin.Samples.Echo")
        )!;

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        string manifestJson = $$"""
            {
              "id": "{{Echo}}",
              "name": "Echo",
              "version": "0.1.0",
              "description": "Sample plugin for the test suite",
              "assembly": "{{EchoDll}}",
              {{(capabilities is null ? string.Empty : $"\"capabilities\": {capabilities},")}}
              "autoEnabled": true
            }
            """;
        File.WriteAllText(Path.Combine(_echoDir, "plugin.json"), manifestJson);

        InstalledPluginFolder.Record(_pluginsDir, _echoDir);
    }

    private sealed class GateServiceProvider(IPluginRunGate gate) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginRunGate) ? gate : null;
    }

    private sealed class StubRevocationStore(PluginRevocationList list) : IPluginRevocationStore
    {
        public PluginRevocationList Current => list;

        public void Save(PluginRevocationList replacement) { }
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPlugin(Ulid id) : IPlugin
    {
        public string Name => "Echo";
        public string Description => "d";
        public Ulid Id => id;
        public Version Version { get; } = new(0, 1, 0);
        public bool Initialized { get; private set; }

        public void Initialize(IPluginContext context) => Initialized = true;

        public void Dispose() { }
    }
}
