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

using NoMercy.MediaProcessing.Common;

namespace NoMercy.Tests.MediaProcessing.Common;

[Trait("Category", "Unit")]
public class OriginCountriesTests
{
    [Fact]
    public void Join_KeepsEveryCountryNotOnlyTheFirst()
    {
        OriginCountries.Join(["US", "JP"]).Should().Be("US,JP");
    }

    [Fact]
    public void Join_EmptyList_StoresNothing()
    {
        OriginCountries.Join([]).Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsBackTheWholeList()
    {
        OriginCountries.Parse("US,JP").Should().Equal("US", "JP");
    }

    // Rows written before the whole list was kept hold a single country.
    [Fact]
    public void Parse_SingleCountryRow_IsAOneItemList()
    {
        OriginCountries.Parse("JP").Should().Equal("JP");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_NothingStored_IsUnknown(string? stored)
    {
        OriginCountries.Parse(stored).Should().BeNull();
    }

    [Fact]
    public void JoinThenParse_RoundTrips()
    {
        string[] countries = ["CN", "JP", "KR"];

        OriginCountries.Parse(OriginCountries.Join(countries)).Should().Equal(countries);
    }
}
