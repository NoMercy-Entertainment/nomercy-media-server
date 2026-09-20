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
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The grant store is where "the owner said yes" is recorded, so the questions
/// that matter are the ones a wrong answer gets dangerous: does an ungranted
/// plugin hold anything, can one plugin's grant satisfy another's check, and
/// does a revoke actually take the permission away.
/// </summary>
public class PluginGrantStoreTests
{
    private static readonly Ulid PluginA = Ulid.Parse("0H248H248H248H248H248H248H");
    private static readonly Ulid PluginB = Ulid.Parse("1248H248H248H248H248H248H2");

    private static IPluginGrantStore Store() => TestPluginPlatform.GrantStore();

    [Fact]
    public void A_plugin_holds_nothing_until_it_is_granted()
    {
        IPluginGrantStore store = Store();

        store.Holds(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com").Should().BeFalse();
        store.Granted(PluginA, PluginGrantKind.NetworkHost).Should().BeEmpty();
    }

    [Fact]
    public void A_grant_is_held_after_it_is_given()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com");

        store.Holds(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com").Should().BeTrue();
    }

    [Fact]
    public void One_plugins_grant_does_not_satisfy_anothers_check()
    {
        // The check keys on the plugin, not just the value. Getting this wrong
        // would mean any installed plugin inherits the permissions of the most
        // trusted one.
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.LibraryWrite, "library-1");

        store.Holds(PluginB, PluginGrantKind.LibraryWrite, "library-1").Should().BeFalse();
    }

    [Fact]
    public void A_grant_of_one_kind_does_not_satisfy_another_kind()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "library-1");

        store.Holds(PluginA, PluginGrantKind.LibraryWrite, "library-1").Should().BeFalse();
    }

    [Fact]
    public void The_everything_value_satisfies_any_value_of_that_kind()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.LibraryWrite, PluginGrant.Everything);

        store.Holds(PluginA, PluginGrantKind.LibraryWrite, "any-library-at-all").Should().BeTrue();
    }

    [Fact]
    public void Revoking_takes_the_permission_away()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com");
        store.Revoke(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com");

        store.Holds(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com").Should().BeFalse();
    }

    [Fact]
    public void Granting_the_same_thing_twice_records_it_once()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "a.example.com");
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "a.example.com");

        store.Granted(PluginA, PluginGrantKind.NetworkHost).Should().ContainSingle();
    }

    [Fact]
    public void A_request_is_pending_until_it_is_answered()
    {
        IPluginGrantStore store = Store();
        store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");

        store
            .PendingRequests()
            .Should()
            .ContainSingle(request =>
                request.PluginId == PluginA && request.Value == "indexer.example.com"
            );
    }

    [Fact]
    public void Asking_repeatedly_does_not_fill_the_owners_dashboard()
    {
        // A plugin retrying in a loop must not be able to bury every other
        // notification the owner has.
        IPluginGrantStore store = Store();

        for (int attempt = 0; attempt < 50; attempt++)
            store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");

        store.PendingRequests().Should().ContainSingle();
    }

    [Fact]
    public void Granting_a_requested_thing_clears_the_request()
    {
        IPluginGrantStore store = Store();
        store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com");

        store.PendingRequests().Should().BeEmpty();
    }

    [Fact]
    public void Something_already_granted_is_not_asked_for_again()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com");
        store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");

        store.PendingRequests().Should().BeEmpty();
    }

    [Fact]
    public void Denying_clears_the_request_without_granting_it()
    {
        IPluginGrantStore store = Store();
        store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");
        store.ClearRequest(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com");

        store.PendingRequests().Should().BeEmpty();
        store.Holds(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com").Should().BeFalse();
    }

    [Fact]
    public async Task A_plugins_own_grants_view_cannot_reach_another_plugin()
    {
        // PluginGrants binds the id at construction precisely so a plugin
        // cannot pass someone else's.
        IPluginGrantStore store = Store();
        store.Grant(PluginB, PluginGrantKind.LibraryWrite, "library-1");

        IPluginGrants grantsForA = TestPluginPlatform.Grants(PluginA, store);

        (await grantsForA.HasAsync(PluginGrantKind.LibraryWrite, "library-1")).Should().BeFalse();
        (await grantsForA.GetAsync(PluginGrantKind.LibraryWrite)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_plugins_request_is_recorded_against_its_own_id()
    {
        IPluginGrantStore store = Store();
        IPluginGrants grants = TestPluginPlatform.Grants(PluginA, store);

        await grants.RequestAsync(PluginGrantKind.NetworkHost, "a.example.com", "because");

        store.PendingRequests().Should().ContainSingle(request => request.PluginId == PluginA);
    }

    [Fact]
    public void Revoking_everything_takes_away_every_kind_including_player_source()
    {
        // The withdraw path used to walk a hand-written list of kinds, so a
        // kind added later - player.source, and every capability.<name> grant
        // built at runtime - survived the owner saying no.
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.NetworkHost, "tracker.example.com");
        store.Grant(PluginA, PluginGrantKind.LibraryWrite, "library-1");
        store.Grant(PluginA, PluginGrantKind.PlayerSource, "ice1.somafm.com");
        store.Grant(
            PluginA,
            PluginGrantKind.ForCapability(PluginCapability.Player),
            PluginGrant.Everything
        );

        store.RevokeAll(PluginA);

        store.Granted(PluginA, PluginGrantKind.NetworkHost).Should().BeEmpty();
        store.Granted(PluginA, PluginGrantKind.LibraryWrite).Should().BeEmpty();
        store.Granted(PluginA, PluginGrantKind.PlayerSource).Should().BeEmpty();
        store
            .Holds(
                PluginA,
                PluginGrantKind.ForCapability(PluginCapability.Player),
                PluginGrant.Everything
            )
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Revoking_everything_leaves_another_plugin_untouched()
    {
        IPluginGrantStore store = Store();
        store.Grant(PluginA, PluginGrantKind.PlayerSource, "ice1.somafm.com");
        store.Grant(PluginB, PluginGrantKind.PlayerSource, "ice1.somafm.com");

        store.RevokeAll(PluginA);

        store.Holds(PluginB, PluginGrantKind.PlayerSource, "ice1.somafm.com").Should().BeTrue();
    }

    [Fact]
    public void Revoking_everything_also_clears_what_the_plugin_was_still_asking_for()
    {
        // A pending request that outlives the withdrawal comes back as a prompt
        // the owner already answered with no.
        IPluginGrantStore store = Store();
        store.Request(PluginA, PluginGrantKind.NetworkHost, "indexer.example.com", "to search");

        store.RevokeAll(PluginA);

        store.PendingRequests().Should().BeEmpty();
    }

    [Fact]
    public async Task An_enormous_reason_is_bounded()
    {
        // The reason renders on the owner's dashboard and is written by a
        // plugin author, so its length is not theirs to decide.
        IPluginGrantStore store = Store();
        IPluginGrants grants = TestPluginPlatform.Grants(PluginA, store);

        await grants.RequestAsync(
            PluginGrantKind.NetworkHost,
            "a.example.com",
            new string('x', 10_000)
        );

        store.PendingRequests().Single().Reason.Length.Should().BeLessThanOrEqualTo(500);
    }
}
