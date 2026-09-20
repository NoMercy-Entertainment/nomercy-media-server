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
using NoMercy.Plugins.Dependencies;
using NoMercy.Plugins.Entitlements;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin that leans on another runs only while that one runs, and nothing
/// is ever bought on the owner's behalf to make that true.
/// </summary>
public class PluginDependencyGateTests
{
    private static readonly Ulid Dependent = Ulid.Parse("01J9ZK5V8Y000000000000000E");
    private static readonly Ulid Dependency = Ulid.Parse("01J9ZK5V8Y000000000000000F");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Noon = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    private static PluginInfo Info(
        Ulid id,
        Version version,
        PluginTier tier = PluginTier.Free,
        PluginStatus status = PluginStatus.Active,
        params PluginDependency[] dependencies
    ) =>
        new()
        {
            Id = id,
            Name = "Sample",
            Description = "d",
            Version = version,
            Status = status,
            Tier = tier,
            Dependencies = dependencies,
        };

    private static PluginDependencyGate Gate(
        PluginInfo dependent,
        PluginInfo? dependency = null,
        bool entitled = false
    ) =>
        new(
            new StubPlugins(dependency is null ? [dependent] : [dependent, dependency]),
            new StubEntitlements(entitled),
            new StubClock(Noon),
            () => Owner
        );

    [Fact]
    public void A_plugin_with_no_dependencies_runs()
    {
        Gate(Info(Dependent, new(1, 0, 0))).Check(Dependent).Should().BeNull();
    }

    [Fact]
    public void A_plugin_this_server_does_not_have_is_not_refused_for_its_dependencies()
    {
        Gate(Info(Dependent, new(1, 0, 0))).Check(Ulid.NewUlid()).Should().BeNull();
    }

    [Fact]
    public void A_missing_dependency_refuses_and_names_it()
    {
        PluginRefusal? refusal = Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    dependencies: new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Free)
                )
            )
            .Check(Dependent);

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyMissing);
        refusal.What.Should().Contain(Dependency.ToString());
        refusal.Fix.Should().Contain("Install");
    }

    [Fact]
    public void A_dependency_outside_the_range_counts_as_missing()
    {
        Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    dependencies: new PluginDependency(new(Dependency), ">=2.0.0", PluginTier.Free)
                ),
                Info(Dependency, new(1, 4, 0))
            )
            .Check(Dependent)!
            .Code.Should()
            .Be(PluginRefusalCodes.DependencyMissing);
    }

    [Fact]
    public void A_paid_dependency_nobody_bought_leaves_the_dependent_off_and_says_where_to_buy_it()
    {
        PluginRefusal? refusal = Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    PluginTier.Paid,
                    dependencies: new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Paid)
                ),
                Info(Dependency, new(1, 0, 0), PluginTier.Paid),
                entitled: false
            )
            .Check(Dependent);

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyPaidNotOwned);
        refusal.Why.Should().Contain("paid");
        refusal.Fix.Should().Contain("nomercy.tv");
    }

    [Fact]
    public void A_paid_dependency_the_owner_holds_runs()
    {
        Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    PluginTier.Paid,
                    dependencies: new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Paid)
                ),
                Info(Dependency, new(1, 0, 0), PluginTier.Paid),
                entitled: true
            )
            .Check(Dependent)
            .Should()
            .BeNull();
    }

    [Fact]
    public void A_dependency_that_was_turned_off_pauses_the_dependent()
    {
        Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    dependencies: new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Free)
                ),
                Info(Dependency, new(1, 0, 0), status: PluginStatus.Disabled)
            )
            .Check(Dependent)!
            .Code.Should()
            .Be(PluginRefusalCodes.DependencyPaused);
    }

    [Fact]
    public void A_free_plugin_may_not_depend_on_a_paid_one()
    {
        PluginRefusal? refusal = Gate(
                Info(
                    Dependent,
                    new(1, 0, 0),
                    dependencies: new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Paid)
                ),
                Info(Dependency, new(1, 0, 0), PluginTier.Paid),
                entitled: true
            )
            .Check(Dependent);

        refusal!.Code.Should().Be(PluginRefusalCodes.DependencyTierMismatch);
        refusal
            .Fix.Should()
            .NotContain(
                "nomercy.tv",
                "sending the owner shopping makes the publisher's mistake theirs"
            );
    }

    [Theory]
    [InlineData("1.4.0", ">=1.0.0", true)]
    [InlineData("1.4.0", ">=2.0.0", false)]
    [InlineData("1.4.0", "<=1.4.0", true)]
    [InlineData("1.4.0", "<1.4.0", false)]
    [InlineData("1.4.0", ">1.0.0", true)]
    [InlineData("1.4.0", "=1.4.0", true)]
    [InlineData("1.4.0", "1.4.0", true)]
    [InlineData("1.4.0", ">=1.0.0 <2.0.0", true)]
    [InlineData("2.0.0", ">=1.0.0 <2.0.0", false)]
    [InlineData("1.4.0", "", true)]
    [InlineData("1.4.0", "not-a-version", false)]
    public void A_range_is_every_clause_holding_at_once(string version, string range, bool allowed)
    {
        PluginSemver.Satisfies(Version.Parse(version), range).Should().Be(allowed);
    }

    [Fact]
    public void The_plan_installs_every_free_dependency_in_the_same_step()
    {
        PluginDependencyResolver resolver = new(
            new StubCatalogue(new(Dependency, PluginTier.Free, new(1, 2, 0))),
            new StubPlugins([])
        );

        resolver
            .Plan([new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Free)])
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Dependency);
    }

    [Fact]
    public void The_plan_never_includes_a_paid_dependency()
    {
        PluginDependencyResolver resolver = new(
            new StubCatalogue(new(Dependency, PluginTier.Paid, new(1, 2, 0))),
            new StubPlugins([])
        );

        resolver
            .Plan([new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Paid)])
            .Should()
            .BeEmpty("a purchase made by pressing install on something else is one nobody made");
    }

    [Fact]
    public void The_plan_skips_a_dependency_that_is_already_here()
    {
        PluginDependencyResolver resolver = new(
            new StubCatalogue(new(Dependency, PluginTier.Free, new(1, 2, 0))),
            new StubPlugins([Info(Dependency, new(1, 2, 0))])
        );

        resolver
            .Plan([new PluginDependency(new(Dependency), ">=1.0.0", PluginTier.Free)])
            .Should()
            .BeEmpty();
    }

    private sealed class StubPlugins(IReadOnlyList<PluginInfo> known) : IPluginManifestSource
    {
        public PluginInfo? Find(Ulid pluginId) => known.FirstOrDefault(info => info.Id == pluginId);

        public IReadOnlyList<PluginInfo> All() => known;
    }

    private sealed class StubCatalogue(PluginCatalogueEntry entry) : IPluginCatalogue
    {
        public PluginCatalogueEntry? Find(Ulid pluginId) =>
            entry.PluginId == pluginId ? entry : null;
    }

    private sealed class StubEntitlements(bool entitled) : IPluginEntitlementStore
    {
        public PluginEntitlementBundle Current =>
            entitled
                ? new(
                    Ulid.Empty,
                    Noon,
                    Noon.AddDays(30),
                    [new(Dependency, Owner, PluginTier.Paid, null, null)]
                )
                : PluginEntitlementBundle.None;

        public void Save(PluginEntitlementBundle replacement) { }
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
