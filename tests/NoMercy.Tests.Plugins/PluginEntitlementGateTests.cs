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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Entitlements;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Dormant, not uninstalled. Someone whose internet is down has not stopped
/// paying, and what they bought is still theirs while the server cannot ask.
/// </summary>
public class PluginEntitlementGateTests
{
    private static readonly Ulid Paid = Ulid.Parse("01J9ZK5V8Y0000000000000002");
    private static readonly Ulid Other = Ulid.Parse("01J9ZK5V8Y0000000000000003");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Housemate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PluginEntitlementGate Gate(PluginEntitlementBundle bundle) =>
        new(new StubEntitlementStore(bundle), new StubClock(Now), Owner);

    private static PluginEntitlementBundle Bundle(
        double refreshDueDaysAgo,
        params PluginEntitlement[] entitlements
    ) =>
        new(
            Ulid.Empty,
            Now.AddDays(-refreshDueDaysAgo - 1),
            Now.AddDays(-refreshDueDaysAgo),
            entitlements
        );

    private static PluginEntitlement Held(
        Ulid plugin,
        Guid? user = null,
        DateTimeOffset? expiresAt = null
    ) => new(plugin, user ?? Owner, PluginTier.Paid, null, expiresAt);

    [Fact]
    public void A_free_plugin_is_never_asked_about()
    {
        Gate(Bundle(-1)).Check(Paid, PluginTier.Free).Should().BeNull();
    }

    [Fact]
    public void A_paid_plugin_the_owner_holds_runs()
    {
        Gate(Bundle(-1, Held(Paid))).Check(Paid, PluginTier.Paid).Should().BeNull();
    }

    [Fact]
    public void A_paid_plugin_with_no_entitlement_is_sent_to_the_shop()
    {
        PluginRefusal? refusal = Gate(Bundle(-1)).Check(Paid, PluginTier.Paid);

        refusal!.Code.Should().Be(PluginRefusalCodes.EntitlementMissing);
        refusal.Fix.Should().Contain("nomercy.tv");
    }

    [Fact]
    public void An_entitlement_for_another_plugin_does_not_cover_this_one()
    {
        Gate(Bundle(-1, Held(Other)))
            .Check(Paid, PluginTier.Paid)!
            .Code.Should()
            .Be(PluginRefusalCodes.EntitlementMissing);
    }

    [Fact]
    public void An_entitlement_held_by_someone_else_on_the_server_is_not_the_owners()
    {
        Gate(Bundle(-1, Held(Paid, Housemate)))
            .Check(Paid, PluginTier.Paid)!
            .Code.Should()
            .Be(PluginRefusalCodes.EntitlementMissing);
    }

    [Fact]
    public void A_lapsed_subscription_reads_as_no_entitlement()
    {
        Gate(Bundle(-1, Held(Paid, expiresAt: Now.AddDays(-1))))
            .Check(Paid, PluginTier.Paid)!
            .Code.Should()
            .Be(PluginRefusalCodes.EntitlementMissing);
    }

    [Fact]
    public void A_subscription_that_has_not_lapsed_yet_runs()
    {
        Gate(Bundle(-1, Held(Paid, expiresAt: Now.AddMinutes(1))))
            .Check(Paid, PluginTier.Paid)
            .Should()
            .BeNull();
    }

    [Fact]
    public void A_subscription_that_lapses_this_very_moment_has_lapsed()
    {
        Gate(Bundle(-1, Held(Paid, expiresAt: Now)))
            .Check(Paid, PluginTier.Paid)!
            .Code.Should()
            .Be(
                PluginRefusalCodes.EntitlementMissing,
                "the moment it expires is the moment it stopped covering anything"
            );
    }

    [Fact]
    public void A_bundle_six_days_overdue_still_runs()
    {
        Gate(Bundle(6, Held(Paid))).Check(Paid, PluginTier.Paid).Should().BeNull();
    }

    [Fact]
    public void A_bundle_exactly_seven_days_overdue_still_runs()
    {
        Gate(Bundle(7, Held(Paid))).Check(Paid, PluginTier.Paid).Should().BeNull();
    }

    [Fact]
    public void A_bundle_eight_days_overdue_goes_dormant()
    {
        PluginRefusal? refusal = Gate(Bundle(8, Held(Paid))).Check(Paid, PluginTier.Paid);

        refusal!.Code.Should().Be(PluginRefusalCodes.EntitlementDormant);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
        refusal.Why.Should().Contain("7 days");
        refusal.Fix.Should().Contain("Nothing is deleted");
    }

    [Fact]
    public void An_owner_who_never_bought_it_is_sent_to_the_shop_even_when_overdue()
    {
        Gate(Bundle(30))
            .Check(Paid, PluginTier.Paid)!
            .Code.Should()
            .Be(
                PluginRefusalCodes.EntitlementMissing,
                "telling someone their offline server cannot confirm a purchase they never made wastes their evening"
            );
    }

    [Fact]
    public void A_server_that_has_never_asked_does_not_run_a_paid_plugin()
    {
        new PluginEntitlementGate(
            new StubEntitlementStore(PluginEntitlementBundle.None),
            new StubClock(Now),
            Owner
        )
            .Check(Paid, PluginTier.Paid)
            .Should()
            .NotBeNull();
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
