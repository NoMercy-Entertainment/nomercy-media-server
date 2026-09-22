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
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Guests;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A guest's plugin runs on somebody else's machine, so it may only hold
/// capabilities whose effects leave when the guest does. What counts as
/// reversible is the contract's answer, not a list kept here.
/// </summary>
public class PluginGuestInstallerTests
{
    private static readonly Guid Guest = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Housemate = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static PluginManifest Manifest(params string[] hooks) =>
        new()
        {
            Id = new(Ulid.NewUlid()),
            Name = "Internet Radio",
            Description = "d",
            Version = "1.0.0",
            TargetAbi = "12.0",
            Assembly = "Sample.dll",
            Capabilities = new() { Hooks = [.. hooks] },
        };

    private static (
        PluginGuestInstaller Installer,
        InMemoryGuestStore Store,
        RecordingPurge Purge
    ) Build()
    {
        InMemoryGuestStore store = new();
        RecordingPurge purge = new();

        return (new(store, purge), store, purge);
    }

    [Fact]
    public void A_guest_plugin_holding_only_reversible_capabilities_installs()
    {
        (PluginGuestInstaller installer, _, _) = Build();

        installer.Install(Manifest("network.fetch"), Guest).Should().BeNull();
    }

    [Fact]
    public void A_guest_plugin_that_writes_the_owners_library_is_refused()
    {
        (PluginGuestInstaller installer, _, _) = Build();

        PluginRefusal? refusal = installer.Install(Manifest("library.write"), Guest);

        refusal!.Code.Should().Be(PluginRefusalCodes.GuestCapabilityIrreversible);
        refusal.What.Should().Contain("library.write");
        refusal.Why.Should().Contain("cannot be undone");
    }

    [Fact]
    public void A_guest_plugin_that_opens_a_listener_is_refused()
    {
        (PluginGuestInstaller installer, _, _) = Build();

        installer
            .Install(Manifest("network.listen"), Guest)!
            .Code.Should()
            .Be(PluginRefusalCodes.GuestCapabilityIrreversible);
    }

    [Fact]
    public void A_guest_plugin_that_starts_a_process_is_refused()
    {
        (PluginGuestInstaller installer, _, _) = Build();

        installer
            .Install(Manifest("process.spawn"), Guest)!
            .Code.Should()
            .Be(PluginRefusalCodes.GuestCapabilityIrreversible);
    }

    [Fact]
    public void One_capability_the_server_cannot_undo_refuses_the_whole_plugin()
    {
        (PluginGuestInstaller installer, InMemoryGuestStore store, _) = Build();
        PluginManifest manifest = Manifest("network.fetch", "library.write");

        installer.Install(manifest, Guest).Should().NotBeNull();

        store
            .GuestFor(manifest.Id.Value)
            .Should()
            .BeNull("a plugin refused halfway is a plugin the server thinks it installed");
    }

    [Fact]
    public void A_capability_the_vocabulary_does_not_carry_is_refused()
    {
        (PluginGuestInstaller installer, _, _) = Build();

        installer
            .Install(Manifest("not.a.capability"), Guest)
            .Should()
            .NotBeNull("guessing in a guest's favour is guessing about somebody else's machine");
    }

    [Fact]
    public void A_guest_plugin_is_recorded_as_that_guests()
    {
        (PluginGuestInstaller installer, InMemoryGuestStore store, _) = Build();
        PluginManifest manifest = Manifest("network.fetch");

        installer.Install(manifest, Guest);

        store.GuestFor(manifest.Id.Value).Should().Be(Guest);
    }

    [Fact]
    public async Task When_a_guest_leaves_their_plugins_and_their_data_go()
    {
        (PluginGuestInstaller installer, InMemoryGuestStore store, RecordingPurge purge) = Build();
        PluginManifest manifest = Manifest("network.fetch");
        installer.Install(manifest, Guest);

        IReadOnlyList<Ulid> removed = await installer.PurgeForAsync(Guest);

        removed.Should().ContainSingle().Which.Should().Be(manifest.Id.Value);
        store.GuestFor(manifest.Id.Value).Should().BeNull();
        purge.Purged.Should().Contain(manifest.Id.Value);
    }

    [Fact]
    public async Task One_guest_leaving_does_not_take_anothers_plugin()
    {
        (PluginGuestInstaller installer, InMemoryGuestStore store, RecordingPurge purge) = Build();
        PluginManifest mine = Manifest("network.fetch");
        PluginManifest theirs = Manifest("network.fetch");
        installer.Install(mine, Guest);
        installer.Install(theirs, Housemate);

        await installer.PurgeForAsync(Guest);

        store.GuestFor(theirs.Id.Value).Should().Be(Housemate);
        purge.Purged.Should().NotContain(theirs.Id.Value);
    }

    [Fact]
    public async Task A_guest_who_installed_nothing_leaves_quietly()
    {
        (PluginGuestInstaller installer, _, RecordingPurge purge) = Build();

        (await installer.PurgeForAsync(Guest)).Should().BeEmpty();
        purge.Purged.Should().BeEmpty();
    }

    [Fact]
    public void The_installs_survive_a_restart()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-guests-{Ulid.NewUlid():N}");
        Ulid pluginId = Ulid.NewUlid();

        try
        {
            new PluginGuestInstallStore(folder).Record(pluginId, Guest);

            new PluginGuestInstallStore(folder).GuestFor(pluginId).Should().Be(Guest);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Forgetting_one_install_survives_a_restart_too()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-guests-{Ulid.NewUlid():N}");
        Ulid pluginId = Ulid.NewUlid();

        try
        {
            PluginGuestInstallStore store = new(folder);
            store.Record(pluginId, Guest);
            store.Forget(pluginId);

            new PluginGuestInstallStore(folder)
                .GuestFor(pluginId)
                .Should()
                .BeNull("a guest who left must not get their plugin back on the next start");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_store_on_disk_gives_back_only_the_guest_that_was_asked_for()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-guests-{Ulid.NewUlid():N}");
        Ulid mine = Ulid.NewUlid();
        Ulid theirs = Ulid.NewUlid();

        try
        {
            PluginGuestInstallStore store = new(folder);
            store.Record(mine, Guest);
            store.Record(theirs, Housemate);
            RecordingPurge purge = new();

            await new PluginGuestInstaller(store, purge).PurgeForAsync(Guest);

            purge
                .Purged.Should()
                .Equal([mine], "one person leaving must not take another person's plugin");
            store.GuestFor(theirs).Should().Be(Housemate);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class InMemoryGuestStore : IPluginGuestInstallStore
    {
        private readonly Dictionary<Ulid, Guid> _installs = [];

        public void Record(Ulid pluginId, Guid guestId) => _installs[pluginId] = guestId;

        public Guid? GuestFor(Ulid pluginId) =>
            _installs.TryGetValue(pluginId, out Guid guest) ? guest : null;

        public IReadOnlyList<Ulid> PluginsFor(Guid guestId) =>
            [.. _installs.Where(entry => entry.Value == guestId).Select(entry => entry.Key)];

        public void Forget(Ulid pluginId) => _installs.Remove(pluginId);
    }

    private sealed class RecordingPurge : IPluginDataPurge
    {
        public List<Ulid> Purged { get; } = [];

        public Task PurgeAsync(Ulid pluginId, CancellationToken ct = default)
        {
            Purged.Add(pluginId);

            return Task.CompletedTask;
        }
    }
}
