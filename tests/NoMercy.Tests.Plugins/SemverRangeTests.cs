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
using NoMercy.Plugins.Manifest;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class SemverRangeTests
{
    [Theory]
    [InlineData("1.0.0", ">=1.0.0", true)]
    [InlineData("0.9.9", ">=1.0.0", false)]
    [InlineData("1.4.0", ">=1.0.0 <2.0.0", true)]
    [InlineData("2.0.0", ">=1.0.0 <2.0.0", false)]
    [InlineData("2.0.0", ">=1.0.0 <=2.0.0", true)]
    [InlineData("1.0.1", ">1.0.0", true)]
    [InlineData("1.0.0", ">1.0.0", false)]
    [InlineData("1.0.0", "", true)]
    [InlineData("1.0.0", "*", true)]
    public void Satisfies_readsTheFiveOperators(string version, string range, bool expected)
    {
        SemverRange.Satisfies(version, range).Should().Be(expected);
    }

    [Theory]
    [InlineData("1.2.0", ">=1.0.0 <1.1.0 || >=1.2.0 <2.0.0", true)]
    [InlineData("1.1.5", ">=1.0.0 <1.1.0 || >=1.2.0 <2.0.0", false)]
    public void Satisfies_takesEitherSideOfAnOr(string version, string range, bool expected)
    {
        SemverRange.Satisfies(version, range).Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-version", ">=1.0.0")]
    [InlineData("1.0.0", ">=not-a-version")]
    public void Satisfies_refusesWhatItCannotRead(string version, string range)
    {
        SemverRange.Satisfies(version, range).Should().BeFalse();
    }

    [Fact]
    public void Satisfies_comparesNumbersNotText()
    {
        SemverRange.Satisfies("1.10.0", ">=1.9.0").Should().BeTrue();
    }
}
