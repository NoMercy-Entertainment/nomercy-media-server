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
using NoMercy.PluginSdk.Manifest;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginDependencyTests
{
    private static PluginManifest Manifest(
        string id,
        PluginTier tier,
        params PluginDependency[] dependencies
    )
    {
        return new PluginManifest
        {
            Id = PluginId.Parse(id),
            Name = id,
            Description = id,
            Version = "1.0.0",
            TargetAbi = "12.0",
            Assembly = $"{id}.dll",
            Entry = $"{id}.Plugin",
            Tier = tier,
            Dependencies = dependencies,
        };
    }

    private const string Radio = "5KTKRT4Z2Y9P59Y40W5CX4TQKF";
    private const string Solver = "1SBQT26FHF98EBRPYVRGD92CZF";

    [Fact]
    public void A_dependency_that_is_installed_and_in_range_resolves()
    {
        PluginRefusal? refusal = PluginDependencyResolver.Resolve(
            Manifest(
                Radio,
                PluginTier.Free,
                new PluginDependency(PluginId.Parse(Solver), ">=1.0.0 <2.0.0", PluginTier.Free)
            ),
            [Manifest(Solver, PluginTier.Free)]
        );

        refusal.Should().BeNull();
    }

    [Fact]
    public void A_dependency_that_is_not_installed_refuses_and_names_it()
    {
        PluginRefusal? refusal = PluginDependencyResolver.Resolve(
            Manifest(
                Radio,
                PluginTier.Free,
                new PluginDependency(PluginId.Parse(Solver), ">=1.0.0", PluginTier.Free)
            ),
            []
        );

        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyMissing);
        refusal.What.Should().Contain(Solver);
    }

    [Fact]
    public void A_version_outside_the_range_refuses()
    {
        PluginRefusal? refusal = PluginDependencyResolver.Resolve(
            Manifest(
                Radio,
                PluginTier.Free,
                new PluginDependency(PluginId.Parse(Solver), ">=2.0.0", PluginTier.Free)
            ),
            [Manifest(Solver, PluginTier.Free)]
        );

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyMissing);
    }

    [Fact]
    public void A_free_plugin_may_not_depend_on_a_paid_one()
    {
        PluginRefusal? refusal = PluginDependencyResolver.Resolve(
            Manifest(
                Radio,
                PluginTier.Free,
                new PluginDependency(PluginId.Parse(Solver), ">=1.0.0", PluginTier.Paid)
            ),
            [Manifest(Solver, PluginTier.Paid)]
        );

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyTierMismatch);
        refusal.Fix.Should().Contain("free");
    }

    [Fact]
    public void A_paid_dependency_nobody_owns_installs_the_plugin_switched_off()
    {
        PluginRefusal? refusal = PluginDependencyResolver.Resolve(
            Manifest(
                Radio,
                PluginTier.Paid,
                new PluginDependency(PluginId.Parse(Solver), ">=1.0.0", PluginTier.Paid)
            ),
            []
        );

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyPaidNotOwned);
        refusal.Why.Should().Contain("never bought for you");
    }
}
