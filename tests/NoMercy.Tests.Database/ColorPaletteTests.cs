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

using NoMercy.Database;

namespace NoMercy.Tests.Database;

// ColorPalette.FromJsonOrNull is the only real logic on this value object: a forgiving
// deserializer that must never let a corrupted DB row take a card render down. Every
// branch (null/empty input, malformed JSON, valid JSON) is asserted here.
public class ColorPaletteTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromJsonOrNull_NullOrEmptyInput_ReturnsNull(string? json)
    {
        ColorPalette? result = ColorPalette.FromJsonOrNull(json);

        Assert.Null(result);
    }

    [Fact]
    public void FromJsonOrNull_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        ColorPalette? result = ColorPalette.FromJsonOrNull("{not valid json");

        Assert.Null(result);
    }

    [Fact]
    public void FromJsonOrNull_ValidJson_DeserializesEveryPaletteSlot()
    {
        const string json = """
            {
                "poster": { "dominant": "#111111", "primary": "#222222" },
                "backdrop": { "dominant": "#333333" },
                "still": { "dominant": "#444444" },
                "profile": { "dominant": "#555555" },
                "image": { "dominant": "#666666" },
                "cover": { "dominant": "#777777" }
            }
            """;

        ColorPalette? result = ColorPalette.FromJsonOrNull(json);

        Assert.NotNull(result);
        Assert.Equal("#111111", result!.Poster!.Dominant);
        Assert.Equal("#222222", result.Poster.Primary);
        Assert.Equal("#333333", result.Backdrop!.Dominant);
        Assert.Equal("#444444", result.Still!.Dominant);
        Assert.Equal("#555555", result.Profile!.Dominant);
        Assert.Equal("#666666", result.Image!.Dominant);
        Assert.Equal("#777777", result.Cover!.Dominant);
    }

    [Fact]
    public void FromJsonOrNull_JsonMissingASlot_LeavesThatSlotNull()
    {
        const string json = """{ "poster": { "dominant": "#111111" } }""";

        ColorPalette? result = ColorPalette.FromJsonOrNull(json);

        Assert.NotNull(result);
        Assert.NotNull(result!.Poster);
        Assert.Null(result.Backdrop);
    }

    // ── IsFinished: what a stored palette string means to the palette queue ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{not valid json")]
    [InlineData("{\"poster\":{}}")]
    [InlineData("{\"poster\":{},\"backdrop\":{}}")]
    public void IsFinished_BlankMalformedOrColourless_IsUnfinished(string? json)
    {
        Assert.False(
            ColorPalette.IsFinished(json),
            "a palette entry without colours is a failed download, not an answer"
        );
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"poster\":{\"dominant\":\"#abc\"}}")]
    [InlineData("{\"poster\":{},\"backdrop\":{\"primary\":\"#def\"}}")]
    public void IsFinished_NoImageMarkerOrAnyColour_IsFinished(string json)
    {
        Assert.True(ColorPalette.IsFinished(json));
    }
}
