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
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A saved revocation list stops the running plugins it names and leaves the
/// rest running (AUD-1134).
/// </summary>
public class PluginRevocationStopsRunningPluginTests : IDisposable
{
    private static readonly Ulid Revoked = Ulid.Parse("01HZX5D3K8QV7W2N4M6P9R0T1A");
    private static readonly Ulid Kept = Ulid.Parse("01HZX5D3K8QV7W2N4M6P9R0T1B");
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _pluginsDir = Path.Combine(
        Path.GetTempPath(),
        "nomercy-revocation-stops-" + Ulid.NewUlid()
    );
    private readonly InMemoryEventBus _eventBus = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginRevocationStore _store;
    private readonly PluginRunGate _gate;
    private readonly PluginManager _manager;

    public PluginRevocationStopsRunningPluginTests()
    {
        Directory.CreateDirectory(_pluginsDir);
        _store = new(Path.Combine(_pluginsDir, "config"), _eventBus);
        _gate = new([
            new PluginRevocationRunCheck(
                new(_store, new StubClock(Now), new(false, NullLogger.Instance)),
                _store
            ),
        ]);
        _manager = new(
            _eventBus,
            new GateServiceProvider(_gate),
            NullLogger<PluginManager>.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            TestStorageHelper.CreateBackend(),
            registry: _registry
        );
    }

    public void Dispose()
    {
        _manager.Dispose();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_running_plugin_named_by_a_saved_list_is_stopped_and_held_disabled()
    {
        RecordingPlugin instance = Running(Revoked);

        _store.Save(new(Now, [new(Revoked, null, null, "plugins.revoked.security")]));

        instance.Disposed.Should().BeTrue("a revoked plugin's instance is stopped (AUD-1134)");
        LoadedPlugin listed = Registered(Revoked);
        listed.Info.Status.Should().Be(PluginStatus.Disabled);
        listed.Instance.Should().BeNull();
        _gate.LastRefusal(Revoked)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_running_plugin_the_saved_list_does_not_name_keeps_running()
    {
        RecordingPlugin revoked = Running(Revoked);
        RecordingPlugin kept = Running(Kept);

        _store.Save(new(Now, [new(Revoked, null, null, "plugins.revoked.security")]));

        revoked.Disposed.Should().BeTrue();
        kept.Disposed.Should().BeFalse("only the named plugin is stopped");
        LoadedPlugin listed = Registered(Kept);
        listed.Info.Status.Should().Be(PluginStatus.Active);
        listed.Instance.Should().BeSameAs(kept);
        _gate.LastRefusal(Kept).Should().BeNull();
    }

    private RecordingPlugin Running(Ulid id)
    {
        RecordingPlugin instance = new(id);
        string folder = Path.Combine(_pluginsDir, id.ToString());

        _registry[id] = new(
            new()
            {
                Id = id,
                Name = "Sample " + id,
                Description = "d",
                Version = new(0, 1, 0),
                Status = PluginStatus.Active,
                AssemblyPath = Path.Combine(folder, "sample.dll"),
                ManifestPath = Path.Combine(folder, "plugin.json"),
            },
            instance,
            null
        );

        return instance;
    }

    private LoadedPlugin Registered(Ulid id)
    {
        _registry.TryGetValue(id, out LoadedPlugin? loaded).Should().BeTrue();
        return loaded!;
    }

    private sealed class GateServiceProvider(IPluginRunGate gate) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginRunGate) ? gate : null;
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPlugin(Ulid id) : IPlugin
    {
        public string Name => "Sample";
        public string Description => "d";
        public Ulid Id => id;
        public Version Version { get; } = new(0, 1, 0);
        public bool Disposed { get; private set; }

        public void Initialize(IPluginContext context) { }

        public void Dispose() => Disposed = true;
    }
}
