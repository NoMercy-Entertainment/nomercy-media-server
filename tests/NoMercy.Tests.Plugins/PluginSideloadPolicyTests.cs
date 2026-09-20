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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Entitlements;
using NoMercy.Plugins.Sideload;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Developer mode is for someone building a plugin. It is not a way around
/// buying one, so a paid id stays refused whatever the owner switches on.
/// </summary>
public class PluginSideloadPolicyTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Housemate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PluginManifest Manifest(PluginTier tier) =>
        new()
        {
            Id = new(Radio),
            Name = "Internet Radio",
            Description = "d",
            Version = "1.0.0",
            TargetAbi = "11.0",
            Assembly = "Sample.dll",
            Tier = tier,
        };

    private static PluginSideloadPolicy Policy(
        bool developerMode,
        params PluginEntitlement[] entitlements
    ) =>
        new(
            () => developerMode,
            new StubEntitlementStore(
                new(Ulid.Empty, Now.AddDays(-1), Now.AddDays(1), entitlements)
            ),
            new StubClock(Now),
            () => Owner
        );

    private static PluginEntitlement Held(Guid? user = null, DateTimeOffset? expiresAt = null) =>
        new(Radio, user ?? Owner, PluginTier.Paid, null, expiresAt);

    [Fact]
    public void With_developer_mode_off_a_file_install_is_refused()
    {
        PluginRefusal? refusal = Policy(developerMode: false).Check(Manifest(PluginTier.Free));

        refusal!.Code.Should().Be(PluginRefusalCodes.SideloadDisabled);
        refusal.Fix.Should().Contain("developer mode");
        refusal.Plugin.Should().Contain("Internet Radio");
    }

    [Fact]
    public void With_developer_mode_on_a_free_plugin_installs()
    {
        Policy(developerMode: true).Check(Manifest(PluginTier.Free)).Should().BeNull();
    }

    [Fact]
    public void A_paid_id_with_no_entitlement_is_refused_even_in_developer_mode()
    {
        PluginRefusal? refusal = Policy(developerMode: true).Check(Manifest(PluginTier.Paid));

        refusal!.Code.Should().Be(PluginRefusalCodes.SideloadPaidId);
        refusal.Why.Should().Contain("paid");
        refusal.Fix.Should().Contain("nomercy.tv");
    }

    [Fact]
    public void A_paid_id_the_owner_bought_installs_from_a_file()
    {
        Policy(developerMode: true, Held())
            .Check(Manifest(PluginTier.Paid))
            .Should()
            .BeNull("someone who paid for it may build against it");
    }

    [Fact]
    public void An_entitlement_someone_else_on_the_server_holds_is_not_the_owners()
    {
        Policy(developerMode: true, Held(Housemate))
            .Check(Manifest(PluginTier.Paid))!
            .Code.Should()
            .Be(PluginRefusalCodes.SideloadPaidId);
    }

    [Fact]
    public void A_lapsed_entitlement_does_not_reopen_the_file_install()
    {
        Policy(developerMode: true, Held(expiresAt: Now.AddDays(-1)))
            .Check(Manifest(PluginTier.Paid))!
            .Code.Should()
            .Be(PluginRefusalCodes.SideloadPaidId);
    }

    [Fact]
    public void Developer_mode_off_is_answered_before_the_tier()
    {
        Policy(developerMode: false)
            .Check(Manifest(PluginTier.Paid))!
            .Code.Should()
            .Be(
                PluginRefusalCodes.SideloadDisabled,
                "telling someone to buy a plugin they cannot install either way sends them to a shop for nothing"
            );
    }

    [Fact]
    public void A_file_install_is_never_shared_with_the_other_people_on_the_server()
    {
        PluginSideloadPolicy.SharedWithMembers.Should().BeFalse();
    }

    [Fact]
    public void A_file_install_never_counts_as_verified()
    {
        PluginSideloadPolicy.Verified.Should().BeFalse();
    }

    [Fact]
    public void Developer_mode_is_off_until_the_owner_turns_it_on()
    {
        PluginDeveloperMode.Off.Enabled.Should().BeFalse();
    }

    [Fact]
    public void The_owners_switch_survives_a_restart()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-devmode-{Ulid.NewUlid():N}");

        try
        {
            new PluginDeveloperMode(true).Save(folder);

            PluginDeveloperMode.Load(folder).Enabled.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_switch_file_nobody_can_read_reads_as_off()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"nm-devmode-{Ulid.NewUlid():N}");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "developer-mode.json"), "{ not json");

            PluginDeveloperMode
                .Load(folder)
                .Enabled.Should()
                .BeFalse(
                    "off is the state where the server can say who wrote every plugin it runs"
                );
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class StubEntitlementStore(PluginEntitlementBundle bundle)
        : IPluginEntitlementStore
    {
        public PluginEntitlementBundle Current => bundle;

        public void Save(PluginEntitlementBundle replacement) { }
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
