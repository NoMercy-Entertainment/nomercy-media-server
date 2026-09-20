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
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Entitlements;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// One answer for every screen. A listing that hides a plugin and a page that
/// opens it have to agree, or someone is offered something that then refuses
/// them.
/// </summary>
public class PluginAccessResolverTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PluginAccessResolver Resolver(
        PluginTier tier,
        bool sideloaded = false,
        Guid? guestFor = null,
        int seatsTaken = 0,
        params PluginEntitlement[] entitlements
    ) =>
        new(
            new StubInstallFacts(tier, sideloaded, guestFor),
            new StubEntitlementStore(
                new(Ulid.Empty, Now.AddDays(-1), Now.AddDays(1), entitlements)
            ),
            new StubMembership([Member], seatsTaken),
            new StubClock(Now),
            () => Owner
        );

    private static PluginEntitlement Held(Guid user, int? seats = null) =>
        new(Radio, user, PluginTier.Paid, seats, null);

    [Fact]
    public void A_free_plugin_is_the_owners_and_shared_with_a_member()
    {
        PluginAccessResolver resolver = Resolver(PluginTier.Free);

        resolver.Resolve(Radio, Owner).Should().Be(PluginAccess.Owned);
        resolver.Resolve(Radio, Member).Should().Be(PluginAccess.Shared);
    }

    [Fact]
    public void Somebody_who_is_not_a_member_sees_nothing()
    {
        Resolver(PluginTier.Free).Resolve(Radio, Stranger).Should().Be(PluginAccess.None);
    }

    [Fact]
    public void A_paid_plugin_the_owner_bought_is_shared_with_every_accepted_member()
    {
        Resolver(PluginTier.Paid, entitlements: Held(Owner))
            .Resolve(Radio, Member)
            .Should()
            .Be(PluginAccess.Shared);
    }

    [Fact]
    public void A_stranger_does_not_get_the_owners_paid_plugin()
    {
        Resolver(PluginTier.Paid, entitlements: Held(Owner))
            .Resolve(Radio, Stranger)
            .Should()
            .Be(PluginAccess.None, "a licence covers the household, not the internet");
    }

    [Fact]
    public void A_paid_plugin_nobody_bought_is_invisible_to_everyone()
    {
        PluginAccessResolver resolver = Resolver(PluginTier.Paid);

        resolver.Resolve(Radio, Owner).Should().Be(PluginAccess.None);
        resolver.Resolve(Radio, Member).Should().Be(PluginAccess.None);
    }

    [Fact]
    public void A_member_who_bought_it_themselves_owns_it_here()
    {
        PluginAccessResolver resolver = Resolver(PluginTier.Paid, entitlements: Held(Member));

        resolver
            .Resolve(Radio, Member)
            .Should()
            .Be(PluginAccess.Owned, "an entitlement is theirs wherever they are signed in");
        resolver.Resolve(Radio, Owner).Should().Be(PluginAccess.None);
    }

    [Fact]
    public void A_stranger_who_bought_it_themselves_still_owns_it_here()
    {
        Resolver(PluginTier.Paid, entitlements: Held(Stranger))
            .Resolve(Radio, Stranger)
            .Should()
            .Be(PluginAccess.Owned);
    }

    [Fact]
    public void A_seat_count_stops_sharing_once_the_seats_are_taken()
    {
        PluginAccessResolver resolver = Resolver(
            PluginTier.Paid,
            seatsTaken: 2,
            entitlements: Held(Owner, seats: 2)
        );

        resolver.Resolve(Radio, Member).Should().Be(PluginAccess.None);
        resolver
            .Resolve(Radio, Owner)
            .Should()
            .Be(PluginAccess.Owned, "the person who bought it does not take one of its seats");
    }

    [Fact]
    public void A_seat_still_free_is_shared()
    {
        Resolver(PluginTier.Paid, seatsTaken: 1, entitlements: Held(Owner, seats: 2))
            .Resolve(Radio, Member)
            .Should()
            .Be(PluginAccess.Shared);
    }

    [Fact]
    public void No_seat_count_means_the_whole_household()
    {
        Resolver(PluginTier.Paid, seatsTaken: 99, entitlements: Held(Owner))
            .Resolve(Radio, Member)
            .Should()
            .Be(PluginAccess.Shared, "null seats is a licence for everyone here, not for nobody");
    }

    [Fact]
    public void A_plugin_installed_from_a_file_is_the_owners_alone()
    {
        PluginAccessResolver resolver = Resolver(PluginTier.Free, sideloaded: true);

        resolver.Resolve(Radio, Owner).Should().Be(PluginAccess.Owned);
        resolver
            .Resolve(Radio, Member)
            .Should()
            .Be(PluginAccess.None, "nothing can say who wrote it, so it is not the household's");
    }

    [Fact]
    public void A_guest_install_belongs_to_that_guest_and_to_nobody_else()
    {
        PluginAccessResolver resolver = Resolver(PluginTier.Free, guestFor: Stranger);

        resolver.Resolve(Radio, Stranger).Should().Be(PluginAccess.Owned);
        resolver.Resolve(Radio, Member).Should().Be(PluginAccess.None);
        resolver
            .Resolve(Radio, Owner)
            .Should()
            .Be(PluginAccess.None, "it was never the server's plugin");
    }

    [Fact]
    public void A_caller_the_server_could_not_identify_sees_nothing()
    {
        Resolver(PluginTier.Free).Resolve(Radio, Guid.Empty).Should().Be(PluginAccess.None);
    }

    [Fact]
    public void An_unidentified_caller_on_a_server_with_no_owner_set_is_not_the_owner()
    {
        PluginAccessResolver resolver = new(
            new StubInstallFacts(PluginTier.Free, false, null),
            new StubEntitlementStore(new(Ulid.Empty, Now.AddDays(-1), Now.AddDays(1), [])),
            new StubMembership([], 0),
            new StubClock(Now),
            () => Guid.Empty
        );

        resolver
            .Resolve(Radio, Guid.Empty)
            .Should()
            .Be(
                PluginAccess.None,
                "both read as an empty guid, and comparing them hands an anonymous request everything the owner has"
            );
    }

    [Fact]
    public void A_server_with_no_owner_set_shares_nothing_with_anyone()
    {
        PluginAccessResolver resolver = new(
            new StubInstallFacts(PluginTier.Free, false, null),
            new StubEntitlementStore(new(Ulid.Empty, Now.AddDays(-1), Now.AddDays(1), [])),
            new StubMembership([], 0),
            new StubClock(Now),
            () => Guid.Empty
        );

        resolver.Resolve(Radio, Member).Should().Be(PluginAccess.None);
    }

    [Fact]
    public void A_lapsed_entitlement_is_no_entitlement()
    {
        PluginAccessResolver resolver = new(
            new StubInstallFacts(PluginTier.Paid, false, null),
            new StubEntitlementStore(
                new(
                    Ulid.Empty,
                    Now.AddDays(-1),
                    Now.AddDays(1),
                    [new PluginEntitlement(Radio, Owner, PluginTier.Paid, null, Now.AddDays(-1))]
                )
            ),
            new StubMembership([Member], 0),
            new StubClock(Now),
            () => Owner
        );

        resolver.Resolve(Radio, Owner).Should().Be(PluginAccess.None);
    }

    private sealed class StubInstallFacts(PluginTier tier, bool sideloaded, Guid? guestFor)
        : IPluginInstallFacts
    {
        public PluginTier TierOf(Ulid pluginId) => tier;

        public bool IsSideloaded(Ulid pluginId) => sideloaded;

        public Guid? GuestFor(Ulid pluginId) => guestFor;
    }

    private sealed class StubEntitlementStore(PluginEntitlementBundle bundle)
        : IPluginEntitlementStore
    {
        public PluginEntitlementBundle Current => bundle;

        public void Save(PluginEntitlementBundle replacement) { }
    }

    private sealed class StubMembership(Guid[] members, int seatsTaken) : IPluginMembership
    {
        public bool IsAcceptedMember(Guid userId) => members.Contains(userId);

        public int SeatsTakenFor(Ulid pluginId) => seatsTaken;
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
