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

public class PluginRefusalCounterTests
{
    private static readonly Ulid One = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Ulid Two = Ulid.Parse("01J9ZK5V8Y0000000000000001");

    private static PluginRefusal Refusal(string code) =>
        new(code, "Plugin 1.0.0", "what", "why", "fix", PluginRefusalSeverity.Blocked);

    [Fact]
    public void A_plugin_that_has_been_refused_nothing_reports_nothing()
    {
        PluginRefusalCounter counter = new();

        counter.Counts(One).Should().BeEmpty();
    }

    [Fact]
    public void The_same_refusal_twice_counts_twice()
    {
        PluginRefusalCounter counter = new();

        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));
        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));

        counter
            .Counts(One)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new KeyValuePair<string, int>(PluginRefusalCodes.CapabilityNotDeclared, 2),
                "one refusal is an author learning the rule; the same one in a loop is a plugin the owner only sees as slow"
            );
    }

    [Fact]
    public void The_most_frequent_comes_first()
    {
        PluginRefusalCounter counter = new();

        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotConsented));
        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));
        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));

        counter
            .Counts(One)
            .Select(entry => entry.Key)
            .Should()
            .Equal([
                PluginRefusalCodes.CapabilityNotDeclared,
                PluginRefusalCodes.CapabilityNotConsented,
            ]);
    }

    [Fact]
    public void One_plugins_refusals_are_not_anothers()
    {
        PluginRefusalCounter counter = new();

        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));

        counter.Counts(Two).Should().BeEmpty();
    }

    [Fact]
    public void Clearing_one_plugin_leaves_the_others_alone()
    {
        PluginRefusalCounter counter = new();
        counter.Count(One, Refusal(PluginRefusalCodes.CapabilityNotDeclared));
        counter.Count(Two, Refusal(PluginRefusalCodes.CapabilityNotDeclared));

        counter.Clear(One);

        counter.Counts(One).Should().BeEmpty();
        counter.Counts(Two).Should().ContainSingle();
    }
}
