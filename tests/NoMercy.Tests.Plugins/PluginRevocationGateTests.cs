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
using NoMercy.Plugins.Revocation;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A revocation names one build, not a plugin, and a server that cannot check
/// pauses rather than guessing. Both matter: the first so a publisher's fix
/// installs without an appeal, the second so "we could not ask" never reads as
/// "nothing was revoked".
/// </summary>
public class PluginRevocationGateTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y0000000000000001");
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PluginRevocationGate Gate(PluginRevocationList list) =>
        new(new StubRevocationStore(list), new StubClock(Now));

    private static PluginRevocationList Aged(double days, params PluginRevocationEntry[] entries) =>
        new(Now.AddDays(-days), entries);

    [Fact]
    public void A_fresh_list_naming_nothing_allows()
    {
        Gate(Aged(0.04)).Check(Radio, Hash).Should().BeNull();
    }

    [Fact]
    public void A_revoked_build_is_refused_and_told_what_to_do()
    {
        PluginRefusal? refusal = Gate(
                Aged(0, new PluginRevocationEntry(Torrent, Hash, "plugins.revoked.security"))
            )
            .Check(Torrent, Hash);

        refusal!.Code.Should().Be(PluginRefusalCodes.Revoked);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
        refusal.Why.Should().Contain("plugins.revoked.security");
        refusal.Fix.Should().Contain("newest version");
    }

    [Fact]
    public void Another_build_of_the_same_plugin_still_runs()
    {
        Gate(Aged(0, new PluginRevocationEntry(Torrent, Hash, "plugins.revoked.security")))
            .Check(Torrent, "a-different-hash")
            .Should()
            .BeNull("a publisher who ships a fix should not need the revocation lifted first");
    }

    [Fact]
    public void The_same_build_under_another_plugins_id_still_runs()
    {
        Gate(Aged(0, new PluginRevocationEntry(Torrent, Hash, "plugins.revoked.security")))
            .Check(Radio, Hash)
            .Should()
            .BeNull();
    }

    [Fact]
    public void The_hash_is_read_without_regard_to_case()
    {
        Gate(Aged(0, new PluginRevocationEntry(Torrent, Hash.ToUpperInvariant(), "k")))
            .Check(Torrent, Hash)
            .Should()
            .NotBeNull("hex is the same build whichever case the publisher wrote it in");
    }

    [Fact]
    public void A_list_six_days_old_still_allows()
    {
        Gate(Aged(6)).Check(Radio, Hash).Should().BeNull();
    }

    [Fact]
    public void A_list_eight_days_old_pauses_every_plugin_including_free_ones()
    {
        PluginRefusal? refusal = Gate(Aged(8)).Check(Radio, Hash);

        refusal!.Code.Should().Be(PluginRefusalCodes.RevocationListStale);
        refusal.Why.Should().Contain("7 days");
        refusal.Fix.Should().Contain("nothing you approved was lost");
    }

    [Fact]
    public void A_list_exactly_seven_days_old_still_allows()
    {
        Gate(Aged(7))
            .Check(Radio, Hash)
            .Should()
            .BeNull("the window is seven days, so the last moment of it is inside");
    }

    [Fact]
    public void A_server_that_has_never_heard_pauses()
    {
        Gate(PluginRevocationList.None)
            .Check(Radio, Hash)!
            .Code.Should()
            .Be(PluginRefusalCodes.RevocationListStale);
    }

    [Fact]
    public void A_revoked_build_is_refused_even_when_the_list_is_stale()
    {
        Gate(Aged(30, new PluginRevocationEntry(Torrent, Hash, "plugins.revoked.security")))
            .Check(Torrent, Hash)!
            .Code.Should()
            .Be(
                PluginRefusalCodes.Revoked,
                "the owner should read why this one stopped, not a message about the clock"
            );
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
}
