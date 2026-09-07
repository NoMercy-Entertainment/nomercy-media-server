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

using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginAbiTests
{
    [Theory]
    [InlineData([null, true])]
    [InlineData(["", true])]
    [InlineData(["10.0", true])]
    [InlineData(["10.1", true])]
    [InlineData(["10.2", false])]
    [InlineData(["9.0", false])]
    [InlineData(["9.5", false])]
    [InlineData(["11.0", false])]
    [InlineData(["not-a-version", false])]
    public void IsCompatible_AppliesMajorMatchMinorCeiling(string? targetAbi, bool expected)
    {
        Assert.Equal(expected, PluginAbi.IsCompatible(targetAbi));
    }

    [Fact]
    public void Current_IsTenOne()
    {
        Assert.Equal(new Version(10, 1), PluginAbi.Current);
    }

    /// <summary>
    /// The promise a minor bump makes. Every plugin built against any earlier
    /// minor of this major keeps loading, which is what lets the contract grow
    /// without a self-hosted user finding their plugins dead after an update.
    /// </summary>
    [Fact]
    public void IsCompatible_AcceptsEveryEarlierMinorOfTheCurrentMajor()
    {
        for (int minor = 0; minor <= PluginAbi.Current.Minor; minor++)
        {
            string targetAbi = $"{PluginAbi.Current.Major}.{minor}";

            Assert.True(
                PluginAbi.IsCompatible(targetAbi),
                $"a plugin targeting ABI {targetAbi} must still load"
            );
        }
    }
}
