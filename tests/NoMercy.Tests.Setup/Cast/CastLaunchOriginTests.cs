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
using NoMercy.Setup.Cast;
using Xunit;

namespace NoMercy.Tests.Setup.Cast;

[Trait("Category", "Unit")]
public class CastLaunchOriginTests
{
    [Theory]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    [InlineData("nl-NL,nl;q=0.9,en;q=0.8", "nl-NL")]
    [InlineData("de;q=0.7", "de")]
    [InlineData(" ;q=1", "en-US")]
    public void SenderLocale_TakesTheFirstLanguageTag(string? header, string expected)
    {
        CastLaunchOrigin.SenderLocale(header).Should().Be(expected);
    }
}
