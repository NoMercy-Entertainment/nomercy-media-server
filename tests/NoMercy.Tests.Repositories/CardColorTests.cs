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
using Newtonsoft.Json;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using Xunit;

namespace NoMercy.Tests.Repositories;

// Oracle: the web app's pickPaletteColor (app-web src/lib/colorHelper.ts) with
// its defaults dark=40, light=120. Each row is that function's answer; its ""
// is null here. The boundary rows (#282828, #787878) follow the same double
// arithmetic, so 40 * 0.2126 + ... lands just under 40 on both sides.
public class CardColorTests
{
    [Theory]
    [InlineData(
        """{"darkVibrant":"#404040","primary":"#505050","dominant":"#606060"}""",
        "#404040"
    )]
    [InlineData(
        """{"darkVibrant":"#101010","primary":"#505050","dominant":"#606060"}""",
        "#505050"
    )]
    [InlineData(
        """{"darkVibrant":"#101010","primary":"#f0f0f0","dominant":"#606060"}""",
        "#606060"
    )]
    [InlineData("""{"darkVibrant":"#101010","primary":"#f0f0f0","dominant":"#202020"}""", null)]
    [InlineData(
        """{"darkVibrant":"#101010","primary":"#f0f0f0","dominant":"#202020","lightVibrant":"#ffffff","darkMuted":"#000000","lightMuted":"#3c5a78"}""",
        "#3c5a78"
    )]
    [InlineData("""{"primary":"#505050","dominant":"#606060"}""", null)]
    [InlineData("""{"darkVibrant":"#555"}""", "#555")]
    [InlineData("""{"darkVibrant":"rgb(80 90 100)"}""", "rgb(80 90 100)")]
    [InlineData("""{"darkVibrant":"#101010","dominant":"#606060"}""", "#606060")]
    [InlineData("""{"darkVibrant":"#282828"}""", null)]
    [InlineData("""{"darkVibrant":"#787878"}""", "#787878")]
    [InlineData("""{"darkVibrant":"#797979"}""", null)]
    [InlineData("""{"darkVibrant":"#272727"}""", null)]
    [InlineData("""{"darkVibrant":"#c01010","primary":"#1040a0"}""", "#c01010")]
    [InlineData("""{"darkVibrant":"","primary":"#505050"}""", null)]
    public void Pick_MatchesTheWebPicker(string paletteJson, string? expected)
    {
        PaletteColors? palette = JsonConvert.DeserializeObject<PaletteColors>(paletteJson);

        CardColor.Pick(palette).Should().Be(expected);
    }

    [Fact]
    public void Pick_WithoutPalette_IsNull()
    {
        CardColor.Pick(null).Should().BeNull();
    }
}
