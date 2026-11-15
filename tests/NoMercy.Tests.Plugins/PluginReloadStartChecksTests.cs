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
using NoMercy.PluginSdk.Verification;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A reload of a plugin the registry already knows asks the start checks a
/// first load asks, and keeps what the first load recorded (AUD-1016).
/// </summary>
public class PluginReloadStartChecksTests : IDisposable
{
    private const string EchoDll = "NoMercy.Plugin.Samples.Echo.dll";
    private const string NotEntitled = "PLUGIN_TEST_NOT_ENTITLED";

    private static readonly Ulid Echo = Ulid.Parse("01ECH000000000000000000000");

    private readonly string _pluginsDir = Path.Combine(
        Path.GetTempPath(),
        "nomercy-reload-checks-" + Ulid.NewUlid()
    );
    private readonly string _echoDir;
    private readonly InMemoryEventBus _eventBus = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginConsentService _consent = new(new InMemoryConsentStore());

    public PluginReloadStartChecksTests()
    {
        _echoDir = Path.Combine(_pluginsDir, "Echo");
        Directory.CreateDirectory(_echoDir);
        StageEcho();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_enable_keeps_what_the_first_load_recorded_about_the_plugin()
    {
        _registry[Echo] = Listed(verified: true, trusted: true, sideloaded: true, services: true);

        await Lifecycle(Passing()).EnablePluginAsync(Echo);

        PluginInfo info = Registered().Info;
        info.Status.Should().Be(PluginStatus.Active);
        info.Verified.Should().BeTrue();
        info.Trusted.Should().BeTrue();
        info.Sideloaded.Should().BeTrue();
        info.ContributesServices.Should().BeTrue();
        info.ManifestPath.Should().Be(Path.Combine(_echoDir, "plugin.json"));
    }

    [Fact]
    public async Task A_reload_the_run_gate_refuses_leaves_the_plugin_off()
    {
        _registry[Echo] = Listed();

        await Loader(Refusing()).LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll));

        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
    }

    [Fact]
    public async Task A_reload_without_the_owners_consent_leaves_the_plugin_off()
    {
        _registry[Echo] = Listed(capabilities: new() { Rest = true });

        await Loader(Passing()).LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll));

        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
    }

    [Fact]
    public async Task A_reload_with_the_owners_consent_starts_the_plugin()
    {
        PluginCapabilities capabilities = new() { Rest = true };
        _registry[Echo] = Listed(capabilities: capabilities);
        _consent.GrantConsent(Echo, capabilities, new(0, 1, 0));

        await Loader(Passing()).LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll));

        Registered().Info.Status.Should().Be(PluginStatus.Active);
        Registered().Instance.Should().NotBeNull();
    }

    [Fact]
    public async Task A_reload_after_an_update_unloaded_the_plugin_is_refused_and_stays_listed()
    {
        await Loader(Refusing())
            .LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll), previous: Info());

        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
    }

    [Fact]
    public async Task A_reload_after_an_update_unloaded_the_plugin_keeps_what_was_recorded()
    {
        await Loader(Passing())
            .LoadPluginAssemblyAsync(
                Path.Combine(_echoDir, EchoDll),
                previous: Info(verified: true, trusted: true, sideloaded: true, services: true)
            );

        PluginInfo info = Registered().Info;
        info.Status.Should().Be(PluginStatus.Active);
        info.Verified.Should().BeTrue();
        info.Trusted.Should().BeTrue();
        info.Sideloaded.Should().BeTrue();
        info.ContributesServices.Should().BeTrue();
    }

    [Fact]
    public async Task A_first_load_of_a_bare_assembly_starts_as_before()
    {
        await Loader(Refusing()).LoadPluginAssemblyAsync(Path.Combine(_echoDir, EchoDll));

        Registered().Info.Status.Should().Be(PluginStatus.Active);
        Registered().Instance.Should().NotBeNull();
        Registered().Info.Sideloaded.Should().BeFalse();
    }

    private LoadedPlugin Registered()
    {
        _registry.TryGetValue(Echo, out LoadedPlugin? loaded).Should().BeTrue();
        return loaded!;
    }

    private LoadedPlugin Listed(
        bool verified = false,
        bool trusted = false,
        bool sideloaded = false,
        bool services = false,
        PluginCapabilities? capabilities = null
    ) => new(Info(verified, trusted, sideloaded, services, capabilities), null, null);

    private PluginInfo Info(
        bool verified = false,
        bool trusted = false,
        bool sideloaded = false,
        bool services = false,
        PluginCapabilities? capabilities = null
    ) =>
        new()
        {
            Id = Echo,
            Name = "Echo",
            Description = "d",
            Version = new(0, 1, 0),
            Status = PluginStatus.Disabled,
            AssemblyPath = Path.Combine(_echoDir, EchoDll),
            ManifestPath = Path.Combine(_echoDir, "plugin.json"),
            Verified = verified,
            Trusted = trusted,
            Sideloaded = sideloaded,
            ContributesServices = services,
            Capabilities = capabilities,
        };

    private PluginRunGate Passing() => new([new PluginConsentRunCheck(_consent)]);

    private PluginRunGate Refusing() =>
        new([new RefusingCheck(Echo), new PluginConsentRunCheck(_consent)]);

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

    private void StageEcho()
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
              "autoEnabled": true
            }
            """;
        File.WriteAllText(Path.Combine(_echoDir, "plugin.json"), manifestJson);

        InstalledPluginFolder.Record(_pluginsDir, _echoDir);
    }

    private sealed class RefusingCheck(Ulid plugin) : IPluginRunCheck
    {
        public PluginRefusal? Check(PluginInfo info) =>
            info.Id == plugin
                ? new(
                    NotEntitled,
                    info.Name,
                    "The plugin does not run.",
                    "A start check said no.",
                    "Nothing to do in a test.",
                    PluginRefusalSeverity.Blocked
                )
                : null;
    }

    private sealed class GateServiceProvider(IPluginRunGate gate) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginRunGate) ? gate : null;
    }
}
