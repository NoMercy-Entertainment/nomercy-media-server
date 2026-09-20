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
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Backs <see cref="ConfigPluginConsentStore"/> with a real
/// <see cref="PluginConfiguration"/> over a real temp-directory-scoped
/// <see cref="NoMercy.Storage.LocalStorage"/> — no mocking of either
/// collaborator — so persistence-across-instances behavior (the store's whole
/// reason for existing over an in-memory HashSet) is genuinely exercised.
/// </summary>
public class ConfigPluginConsentStoreTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigPluginConsentStoreTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-consent-store-tests-" + Ulid.NewUlid().ToString()
        );
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private ConfigPluginConsentStore MakeStore() =>
        new(new PluginConfiguration(_tempDir, TestStorageHelper.CreateStorage(_tempDir)));

    [Fact]
    public void Contains_NoConfigFileYet_ReturnsFalse()
    {
        ConfigPluginConsentStore store = MakeStore();

        store.Contains(Ulid.NewUlid()).Should().BeFalse();
    }

    [Fact]
    public void Add_ThenContains_ReturnsTrue()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid id = Ulid.NewUlid();

        store.Add(id, null, new Version(1, 0));

        store.Contains(id).Should().BeTrue();
    }

    [Fact]
    public void Add_PersistsAcrossNewStoreInstances()
    {
        // The whole point of a config-backed store over an in-memory HashSet:
        // a second store instance reading the SAME config file must observe
        // the grant a completely different store instance made.
        Ulid id = Ulid.NewUlid();
        MakeStore().Add(id, null, new Version(1, 0));

        ConfigPluginConsentStore secondInstance = MakeStore();

        secondInstance.Contains(id).Should().BeTrue();
    }

    [Fact]
    public void Add_SameIdTwice_DoesNotDuplicateOrThrow()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid id = Ulid.NewUlid();

        store.Add(id, null, new Version(1, 0));
        Action act = () => store.Add(id, null, new Version(1, 0));

        act.Should().NotThrow();
        store.Contains(id).Should().BeTrue();
    }

    [Fact]
    public void Add_SecondDifferentId_BothPersist()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid first = Ulid.NewUlid();
        Ulid second = Ulid.NewUlid();

        store.Add(first, null, new Version(1, 0));
        store.Add(second, null, new Version(1, 0));

        store.Contains(first).Should().BeTrue();
        store.Contains(second).Should().BeTrue();
    }

    [Fact]
    public void Remove_NoConfigFileYet_DoesNotThrow()
    {
        ConfigPluginConsentStore store = MakeStore();

        Action act = () => store.Remove(Ulid.NewUlid());

        act.Should().NotThrow();
    }

    [Fact]
    public void Remove_IdNotGranted_DoesNotThrowAndLeavesOthersIntact()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid granted = Ulid.NewUlid();
        store.Add(granted, null, new Version(1, 0));

        Action act = () => store.Remove(Ulid.NewUlid());

        act.Should().NotThrow();
        store.Contains(granted).Should().BeTrue();
    }

    [Fact]
    public void Remove_GrantedId_RemovesIt()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid id = Ulid.NewUlid();
        store.Add(id, null, new Version(1, 0));

        store.Remove(id);

        store.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void Remove_PersistsAcrossNewStoreInstances()
    {
        Ulid id = Ulid.NewUlid();
        ConfigPluginConsentStore first = MakeStore();
        first.Add(id, null, new Version(1, 0));
        first.Remove(id);

        ConfigPluginConsentStore second = MakeStore();

        second.Contains(id).Should().BeFalse();
    }

    [Fact]
    public void Add_RecordsCapabilitiesAndManifestVersion_AndGetReturnsThem()
    {
        ConfigPluginConsentStore store = MakeStore();
        Ulid id = Ulid.NewUlid();
        PluginCapabilities capabilities = new() { Hooks = ["auth"] };

        store.Add(id, capabilities, new Version(2, 1));

        PluginConsentGrant? grant = store.Get(id);
        grant.Should().NotBeNull();
        grant!.Capabilities!.Hooks.Should().Contain("auth");
        grant.ManifestVersion.Should().Be("2.1");
    }

    [Fact]
    public void Get_PreUpgradeGrantedId_ReturnsGrantWithNoCapabilities()
    {
        Guid preUpgradeId = Guid.Parse("395df423-3e2f-4a1c-bc5b-dbc41a9133ef");
        File.WriteAllText(
            Path.Combine(_tempDir, "config.json"),
            $@"{{""GrantedPluginIds"":[""{preUpgradeId}""]}}"
        );

        ConfigPluginConsentStore store = MakeStore();

        PluginConsentGrant? grant = store.Get(new(preUpgradeId));
        grant.Should().NotBeNull();
        grant!.Capabilities.Should().BeNull();
    }

    [Fact]
    public void Contains_ConsentGrantedBeforeIdsBecameUlid_IsStillHeld()
    {
        // The file a server that ran the GUID build left behind. A malformed
        // config reads as no config, so getting this wrong does not throw —
        // it silently un-consents every plugin the user already approved.
        Guid preUpgradeId = Guid.Parse("395df423-3e2f-4a1c-bc5b-dbc41a9133ef");
        File.WriteAllText(
            Path.Combine(_tempDir, "config.json"),
            $@"{{""GrantedPluginIds"":[""{preUpgradeId}""]}}"
        );

        ConfigPluginConsentStore store = MakeStore();

        store.Contains(new(preUpgradeId)).Should().BeTrue();
    }
}
