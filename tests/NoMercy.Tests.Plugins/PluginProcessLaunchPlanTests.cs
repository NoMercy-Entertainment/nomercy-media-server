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
using NoMercy.PluginSdk.Ipc;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Quotas;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What the child process is told is the whole trust relationship.
/// <para>
/// Get the token wrong and every call the plugin makes is refused; get the
/// endpoint wrong and it never connects at all. Both look identical from
/// outside — a plugin that does nothing — so the plan is asserted here rather
/// than discovered by starting a process and watching it be silent.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginProcessLaunchPlanTests
{
    [Fact]
    public void ThePlan_CarriesEveryVariableTheHostRefusesToStartWithout()
    {
        PluginProcessLaunchPlan plan = Plan();

        plan.Environment.Should()
            .ContainKeys(
                PluginChannelEnvironment.PluginId,
                PluginChannelEnvironment.AssemblyPath,
                PluginChannelEnvironment.DataFolder,
                PluginChannelEnvironment.BrokerEndpoint,
                PluginChannelEnvironment.HostEndpoint,
                PluginChannelEnvironment.Token
            );
    }

    /// <summary>
    /// A token only has to outlive the process it belongs to. One that
    /// survived a restart would still open the channel of a plugin that had
    /// been shut down for abusing it.
    /// </summary>
    [Fact]
    public void EveryLaunch_GetsItsOwnToken()
    {
        Ulid plugin = Ulid.NewUlid();

        Plan(plugin).Token.Should().NotBe(Plan(plugin).Token);
    }

    [Fact]
    public void TheTokenIsLongEnoughToBeWorthHaving()
    {
        Plan().Token.Length.Should().BeGreaterThanOrEqualTo(32);
    }

    [Fact]
    public void TheEndpoints_AreTheOnesThisPluginsChannelUses()
    {
        Ulid plugin = Ulid.NewUlid();
        PluginProcessLaunchPlan plan = Plan(plugin);

        plan.Environment[PluginChannelEnvironment.BrokerEndpoint]
            .Should()
            .Be(PluginChannelEndpoints.BrokerFor(plugin));
        plan.Environment[PluginChannelEnvironment.HostEndpoint]
            .Should()
            .Be(PluginChannelEndpoints.HostFor(plugin));
    }

    /// <summary>
    /// An empty quota variable reads to the child as a quota of zero, which is
    /// a plugin that may use nothing rather than one nobody limited.
    /// </summary>
    [Fact]
    public void NoQuota_MeansTheVariableIsAbsentRatherThanEmpty()
    {
        PluginProcessLaunchPlan plan = Plan();

        plan.Environment.Should()
            .NotContainKey(PluginChannelEnvironment.MemoryQuotaBytes)
            .And.NotContainKey(PluginChannelEnvironment.CpuQuotaPercent);
    }

    [Fact]
    public void AQuota_TravelsWithTheLaunch()
    {
        PluginProcessLaunchPlan plan = Plan(quota: new PluginQuota(25, 1024, 2048, 4096));

        plan.Environment[PluginChannelEnvironment.MemoryQuotaBytes].Should().Be("1024");
        plan.Environment[PluginChannelEnvironment.CpuQuotaPercent].Should().Be("25");
    }

    private static PluginProcessLaunchPlan Plan(Ulid? pluginId = null, PluginQuota? quota = null) =>
        PluginProcessLaunchPlan.For(
            pluginId ?? Ulid.NewUlid(),
            "C:/plugins/radio/Radio.dll",
            "C:/plugins/data/radio",
            quota
        );
}
