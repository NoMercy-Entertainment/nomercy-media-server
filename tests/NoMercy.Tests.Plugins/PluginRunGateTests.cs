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
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Four questions, one order. A withdrawn build that is also unpaid must say
/// it was withdrawn: an owner told to buy something would buy it and watch
/// nothing change.
/// </summary>
public class PluginRunGateTests
{
    private static readonly Ulid Plugin = Ulid.Parse("01J9ZK5V8Y0000000000000014");

    private static readonly PluginInfo Info = new()
    {
        Id = Plugin,
        Name = "Sample",
        Description = "d",
        Version = new(1, 0, 0),
        Status = PluginStatus.Active,
    };

    private static PluginRunGate Gate(
        string? revocation = null,
        string? entitlement = null,
        string? dependency = null,
        string? consent = null
    ) =>
        new([
            new StubCheck(revocation),
            new StubCheck(entitlement),
            new StubCheck(dependency),
            new StubCheck(consent),
        ]);

    [Fact]
    public void Every_gate_happy_means_the_run_gate_says_yes()
    {
        Gate().MayRun(Info).Should().BeNull();
    }

    [Fact]
    public void A_revoked_plugin_says_revoked_even_when_the_entitlement_is_also_missing()
    {
        Gate(
                revocation: PluginRefusalCodes.Revoked,
                entitlement: PluginRefusalCodes.EntitlementMissing
            )
            .MayRun(Info)!
            .Code.Should()
            .Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_stale_list_pauses_before_anything_else_is_asked()
    {
        Gate(
                revocation: PluginRefusalCodes.RevocationListStale,
                dependency: PluginRefusalCodes.DependencyMissing
            )
            .MayRun(Info)!
            .Code.Should()
            .Be(PluginRefusalCodes.RevocationListStale);
    }

    [Fact]
    public void A_missing_entitlement_beats_a_missing_dependency()
    {
        Gate(
                entitlement: PluginRefusalCodes.EntitlementMissing,
                dependency: PluginRefusalCodes.DependencyMissing
            )
            .MayRun(Info)!
            .Code.Should()
            .Be(PluginRefusalCodes.EntitlementMissing);
    }

    [Fact]
    public void A_dependency_problem_beats_a_missing_consent()
    {
        Gate(
                dependency: PluginRefusalCodes.DependencyPaused,
                consent: PluginRefusalCodes.CapabilityNotConsented
            )
            .MayRun(Info)!
            .Code.Should()
            .Be(PluginRefusalCodes.DependencyPaused);
    }

    [Fact]
    public void A_question_after_the_first_refusal_is_not_asked_at_all()
    {
        StubCheck first = new(PluginRefusalCodes.Revoked);
        StubCheck second = new(PluginRefusalCodes.EntitlementMissing);

        new PluginRunGate([first, second]).MayRun(Info);

        second
            .Asked.Should()
            .Be(0, "a gate that runs anyway is a gate that can log or charge for nothing");
    }

    [Fact]
    public void The_refusal_is_remembered_so_the_health_page_can_show_it()
    {
        PluginRunGate gate = Gate(revocation: PluginRefusalCodes.Revoked);

        gate.MayRun(Info);

        gate.LastRefusal(Plugin)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public void A_plugin_that_runs_again_stops_showing_last_weeks_reason()
    {
        StubCheck check = new(PluginRefusalCodes.Revoked);
        PluginRunGate gate = new([check]);
        gate.MayRun(Info);

        check.Code = null;
        gate.MayRun(Info);

        gate.LastRefusal(Plugin).Should().BeNull();
    }

    [Fact]
    public void One_plugins_refusal_is_not_anothers()
    {
        PluginRunGate gate = Gate(revocation: PluginRefusalCodes.Revoked);

        gate.MayRun(Info);

        gate.LastRefusal(Ulid.NewUlid()).Should().BeNull();
    }

    private sealed class StubCheck(string? code) : IPluginRunCheck
    {
        public string? Code { get; set; } = code;

        public int Asked { get; private set; }

        public PluginRefusal? Check(PluginInfo plugin)
        {
            Asked++;

            return Code is null
                ? null
                : new(Code, plugin.Id.ToString(), "w", "y", "f", PluginRefusalSeverity.Blocked);
        }
    }
}
