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
using NoMercy.Data.Plugins;
using NoMercy.Database.Models.Music;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The one place the plugin's stem coverage and the database's own are
/// translated into each other. Both directions are pinned here so a kind
/// added to one enum and forgotten on the other fails a test instead of
/// throwing inside a plugin's sweep.
/// </summary>
public class StemCoverageMapTests
{
    [Theory]
    [InlineData(PluginStemCoverage.Full)]
    [InlineData(PluginStemCoverage.MixIn)]
    [InlineData(PluginStemCoverage.MixOut)]
    public void EveryCoverage_RoundTrips(PluginStemCoverage coverage)
    {
        StemCoverage stored = StemCoverageMap.ToDb(coverage);

        StemCoverageMap.ToPlugin(stored).Should().Be(coverage);
    }

    [Fact]
    public void EveryPluginCoverage_IsMapped()
    {
        foreach (PluginStemCoverage coverage in Enum.GetValues<PluginStemCoverage>())
        {
            Func<StemCoverage> map = () => StemCoverageMap.ToDb(coverage);

            map.Should().NotThrow($"{coverage} has no database coverage to map to");
        }
    }

    [Fact]
    public void EveryStoredCoverage_IsMapped()
    {
        foreach (StemCoverage coverage in Enum.GetValues<StemCoverage>())
        {
            Func<PluginStemCoverage> map = () => StemCoverageMap.ToPlugin(coverage);

            map.Should().NotThrow($"{coverage} has no plugin coverage to map to");
        }
    }

    /// <summary>
    /// The mapper can only stay total if the two enums carry the same members:
    /// a kind added to one side alone would otherwise be caught here rather
    /// than by an <see cref="ArgumentOutOfRangeException" /> mid-sweep.
    /// </summary>
    [Fact]
    public void BothEnums_CarryTheSameMembers()
    {
        Enum.GetNames<PluginStemCoverage>().Should().BeEquivalentTo(Enum.GetNames<StemCoverage>());
    }
}
