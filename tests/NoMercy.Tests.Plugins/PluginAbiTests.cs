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
    [InlineData(["10.2", true])]
    [InlineData(["10.9", true])]
    [InlineData(["11.0", true])]
    [InlineData(["11.1", false])]
    [InlineData(["9.5", false])]
    [InlineData(["12.0", false])]
    [InlineData(["not-a-version", false])]
    public void IsCompatible_AcceptsThisMajorAndTheWholePrevious(string? targetAbi, bool expected)
    {
        Assert.Equal(expected, PluginAbi.IsCompatible(targetAbi));
    }

    [Fact]
    public void Current_IsElevenZero()
    {
        Assert.Equal(new Version(11, 0), PluginAbi.Current);
    }
}
