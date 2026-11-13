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
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Release 1 of marketplace trust only watches. Every place that would stop a
/// plugin says so in the log, once, and lets it run; the one switch
/// Plugins:Trust:Enforce brings the hard answer back unchanged.
/// </summary>
public class PluginTrustWarnOnlyTests
{
    private static readonly Ulid Torrent = Ulid.Parse("01J9ZK5V8Y0000000000000001");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly Version Installed = new(1, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static PluginRevocationGate RevocationGate(
        PluginRevocationList list,
        PluginTrustMode mode
    ) => new(new StubRevocationStore(list), new StubClock(Now), mode);

    private static PluginVerificationContext UnknownKeyContext() =>
        new()
        {
            Manifest = new()
            {
                Id = new(Ulid.NewUlid()),
                Name = "Sample",
                Description = "d",
                Version = "1.0.0",
                TargetAbi = "12.0",
                Assembly = "Sample.dll",
            },
            AssemblyPath = "Sample.dll",
            Signature = new("ed25519", "stranger", "c2ln"),
            FromMarketplace = true,
        };

    private static PluginRevocationList Revoking() =>
        new(Now, [new PluginRevocationEntry(Torrent, "1.0.0", Hash, "plugins.revoked.security")]);

    private static readonly PluginInfo TorrentPlugin = new()
    {
        Id = Torrent,
        Name = "Sample",
        Description = "d",
        Version = Installed,
        Status = PluginStatus.Active,
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_revoked_build_is_refused_in_every_trust_mode(bool enforce)
    {
        PluginRefusal? refusal = RevocationGate(Revoking(), new(enforce, new ListLogger()))
            .Check(Torrent, Hash, Installed);

        refusal.Should().NotBeNull("a revocation is not a warning (AUD-1134)");
        refusal!.Code.Should().Be(PluginRefusalCodes.Revoked);
        refusal.Severity.Should().Be(PluginRefusalSeverity.Blocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_revoked_plugin_does_not_pass_the_run_gate_in_any_trust_mode(bool enforce)
    {
        StubRevocationStore store = new(Revoking());
        PluginRunGate runGate = new([
            new PluginRevocationRunCheck(
                new(store, new StubClock(Now), new(enforce, new ListLogger())),
                store
            ),
        ]);

        runGate.MayRun(TorrentPlugin)!.Code.Should().Be(PluginRefusalCodes.Revoked);
        runGate.LastRefusal(Torrent)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_revocation_refusal_is_logged_with_the_plugin_and_the_revocation(bool enforce)
    {
        ListLogger logger = new();

        RevocationGate(Revoking(), new(enforce, logger)).Check(Torrent, Hash, Installed);

        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains(PluginRefusalCodes.Revoked)
                && entry.Message.Contains($"plugin {Torrent}")
                && entry.Message.Contains($"listing {Torrent} version 1.0.0 sha256 {Hash}")
                && !entry.Message.StartsWith(PluginTrustMode.LogPrefix)
            );
    }

    [Fact]
    public void Warn_only_does_not_pause_on_a_stale_list()
    {
        ListLogger logger = new();

        RevocationGate(new(Now.AddDays(-30), []), new(false, logger))
            .Check(Torrent, Hash, Installed)
            .Should()
            .BeNull();

        logger
            .Entries.Should()
            .ContainSingle(entry => entry.Message.Contains(PluginRefusalCodes.RevocationListStale));
    }

    [Fact]
    public void The_same_would_refuse_is_logged_once_not_on_every_check()
    {
        ListLogger logger = new();
        PluginRevocationGate gate = RevocationGate(new(Now.AddDays(-30), []), new(false, logger));

        gate.Check(Torrent, Hash, Installed);
        gate.Check(Torrent, Hash, Installed);
        gate.Check(Torrent, Hash, Installed);

        logger.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void Enforce_brings_the_hard_refusal_back()
    {
        ListLogger logger = new();

        RevocationGate(new(Now.AddDays(-30), []), new(true, logger))
            .Check(Torrent, Hash, Installed)!
            .Severity.Should()
            .Be(PluginRefusalSeverity.Blocked);

        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Warn_only_keeps_a_paid_plugin_running_when_the_bundle_is_overdue()
    {
        ListLogger logger = new();
        PluginEntitlementBundle overdue = new(
            Ulid.Empty,
            Now.AddDays(-20),
            Now.AddDays(-10),
            [new PluginEntitlement(Torrent, Owner, PluginTier.Paid, null, null)]
        );
        PluginEntitlementGate gate = new(
            new StubEntitlementStore(overdue),
            new StubClock(Now),
            Owner,
            new(false, logger)
        );

        gate.Check(Torrent, PluginTier.Paid).Should().BeNull();
        logger
            .Entries.Should()
            .ContainSingle(entry => entry.Message.Contains(PluginRefusalCodes.EntitlementDormant));
    }

    [Fact]
    public void Warn_only_still_refuses_a_paid_plugin_nobody_bought()
    {
        PluginEntitlementGate gate = new(
            new StubEntitlementStore(PluginEntitlementBundle.None),
            new StubClock(Now),
            Owner,
            new(false, new ListLogger())
        );

        gate.Check(Torrent, PluginTier.Paid)!
            .Code.Should()
            .Be(
                PluginRefusalCodes.EntitlementMissing,
                "every released server already refuses this; release 1 only softens the new checks"
            );
    }

    [Fact]
    public void Warn_only_trusts_a_marketplace_package_whose_key_is_unknown()
    {
        ListLogger logger = new();
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = "AAAA" }),
            new(false, logger)
        );

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(UnknownKeyContext());

        outcome.Should().Be(PluginStageOutcome.Trust);
        message.Should().Contain("does not trust");
        logger
            .Entries.Should()
            .ContainSingle(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.StartsWith(PluginTrustMode.LogPrefix)
            );
    }

    [Fact]
    public void With_enforce_an_unknown_key_still_fails()
    {
        SignatureVerificationStage stage = new(
            new PluginTrustedKeys(new Dictionary<string, string> { ["k1"] = "AAAA" }),
            new(true, new ListLogger())
        );

        stage.Evaluate(UnknownKeyContext()).Outcome.Should().Be(PluginStageOutcome.Fail);
    }

    private sealed class StubRevocationStore(PluginRevocationList list) : IPluginRevocationStore
    {
        public PluginRevocationList Current => list;

        public void Save(PluginRevocationList replacement) { }
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
