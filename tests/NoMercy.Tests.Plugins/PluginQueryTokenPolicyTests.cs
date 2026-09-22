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
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginQueryTokenPolicyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("not-a-version", true)]
    [InlineData("10.2", true)]
    [InlineData("11.0", false)]
    [InlineData("12.0", false)]
    public void Accepts_DoesNotMoveWhenTheServerAbiMoves(string? targetAbi, bool expected)
    {
        PluginQueryTokenPolicy.Accepts(targetAbi).Should().Be(expected);
    }

    /// <summary>
    /// The warning is only ever written while the token is still served, so the
    /// band it names has to be the one below the refusal, not whatever major the
    /// host happens to run.
    /// </summary>
    [Fact]
    public void Warning_NamesTheBandThatIsStillServed()
    {
        string warning = PluginQueryTokenPolicy.Warning("Internet Radio");

        warning.Should().Contain("Accepted on ABI 10.x and refused from ABI 11");
    }
}
