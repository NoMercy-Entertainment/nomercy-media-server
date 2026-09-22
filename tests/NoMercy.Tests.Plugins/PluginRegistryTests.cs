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
using System.Collections.Concurrent;
using FluentAssertions;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginRegistryTests
{
    private static LoadedPlugin MakePlugin(Ulid id)
    {
        PluginInfo info = new()
        {
            Id = id,
            Name = "Test",
            Description = string.Empty,
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
        };
        return new(info, null, null);
    }

    [Fact]
    public void Set_TryGetValue_And_Values_RoundTrip()
    {
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();
        LoadedPlugin plugin = MakePlugin(id);

        registry[id] = plugin;

        registry.TryGetValue(id, out LoadedPlugin? found).Should().BeTrue();
        found.Should().BeSameAs(plugin);
        registry.Values.Should().ContainSingle().Which.Should().BeSameAs(plugin);
    }

    [Fact]
    public void TryRemove_RemovesThePlugin()
    {
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();
        registry[id] = MakePlugin(id);

        registry.TryRemove(id, out LoadedPlugin? removed).Should().BeTrue();
        removed.Should().NotBeNull();
        registry.TryGetValue(id, out _).Should().BeFalse();
        registry.Values.Should().BeEmpty();
    }

    [Fact]
    public void TryGetValue_UnknownId_ReturnsFalse()
    {
        PluginRegistry registry = new();

        registry.TryGetValue(Ulid.NewUlid(), out _).Should().BeFalse();
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        PluginRegistry registry = new();
        registry[Ulid.NewUlid()] = MakePlugin(Ulid.NewUlid());
        registry[Ulid.NewUlid()] = MakePlugin(Ulid.NewUlid());

        registry.Clear();

        registry.Values.Should().BeEmpty();
    }

    // ── Dispose+unload on replace ────────────────────────────────────────────
    //
    // Every registry write in PluginLoader (manifest reload, direct-assembly
    // reload, malfunctioned re-record) used to overwrite the previous entry
    // outright: its Instance was never disposed and its LoadContext was never
    // unloaded, leaking a collectible ALC that pins the old assembly file.

    [Fact]
    public void Set_ReplacingExistingEntry_DisposesOldInstanceAndUnloadsOldLoadContext()
    {
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();

        DisposalTrackingPlugin oldInstance = new();
        string dummyPath = Path.Combine(
            Path.GetTempPath(),
            $"nm-registry-test-{Ulid.NewUlid():N}.dll"
        );
        File.WriteAllBytes(dummyPath, []);
        PluginLoadContext oldContext = new(dummyPath);
        bool oldContextUnloaded = false;
        oldContext.Unloading += _ => oldContextUnloaded = true;

        registry[id] = new(MakeInfo(id), oldInstance, oldContext);

        LoadedPlugin newLoaded = new(MakeInfo(id), new DisposalTrackingPlugin(), null);
        registry[id] = newLoaded;

        oldInstance.WasDisposed.Should().BeTrue();
        oldContextUnloaded.Should().BeTrue();
        registry.TryGetValue(id, out LoadedPlugin? current).Should().BeTrue();
        current.Should().BeSameAs(newLoaded);

        try
        {
            File.Delete(dummyPath);
        }
        catch
        {
            // Best-effort — the ALC may still hold the file briefly post-unload.
        }
    }

    [Fact]
    public void Set_ReplacingWithSameReference_DoesNotDisposeIt()
    {
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();
        DisposalTrackingPlugin instance = new();
        LoadedPlugin loaded = new(MakeInfo(id), instance, null);

        registry[id] = loaded;
        registry[id] = loaded; // idempotent re-set of the exact same object

        instance.WasDisposed.Should().BeFalse();
    }

    [Fact]
    public void Set_ReplacingEntryWithNullInstance_DoesNotThrow()
    {
        // A Malfunctioned entry recorded with no instance (e.g. a plugin whose
        // constructor threw) has Instance == null — `replaced.Instance?.Dispose()`
        // exists specifically so replacing THAT entry never calls Dispose on a
        // null reference.
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();
        registry[id] = new(MakeInfo(id), null, null);

        Action act = () => registry[id] = new(MakeInfo(id), new DisposalTrackingPlugin(), null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Set_ConcurrentReplacements_DisposesEverySupersededEntryExactlyOnce()
    {
        PluginRegistry registry = new();
        Ulid id = Ulid.NewUlid();
        const int concurrentWriters = 50;
        ConcurrentBag<DisposalTrackingPlugin> instances = [];

        Parallel.For(
            0,
            concurrentWriters,
            i =>
            {
                DisposalTrackingPlugin instance = new();
                instances.Add(instance);
                registry[id] = new(MakeInfo(id), instance, null);
            }
        );

        int disposedCount = instances.Count(i => i.WasDisposed);
        disposedCount
            .Should()
            .Be(
                concurrentWriters - 1,
                "every superseded entry must be disposed exactly once, and the surviving one must not be"
            );

        registry.TryGetValue(id, out LoadedPlugin? current).Should().BeTrue();
        DisposalTrackingPlugin survivor = (DisposalTrackingPlugin)current!.Instance!;
        survivor.WasDisposed.Should().BeFalse();
    }

    private static PluginInfo MakeInfo(Ulid id) =>
        new()
        {
            Id = id,
            Name = "Test",
            Description = string.Empty,
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
        };

    private sealed class DisposalTrackingPlugin : IPlugin
    {
        public bool WasDisposed { get; private set; }

        public string Name => "DisposalTracker";
        public string Description => string.Empty;
        public Ulid Id => Ulid.NewUlid();
        public Version Version => new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() => WasDisposed = true;
    }
}
