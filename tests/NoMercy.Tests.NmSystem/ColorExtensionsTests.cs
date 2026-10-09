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

using System.Drawing;
using ImageMagick;

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class ColorExtensionsTests
{
    [Theory]
    [InlineData([0, 0, 0, "#000000"])]
    [InlineData([255, 255, 255, "#FFFFFF"])]
    [InlineData([255, 0, 0, "#FF0000"])]
    [InlineData([0, 255, 0, "#00FF00"])]
    [InlineData([0, 0, 255, "#0000FF"])]
    [InlineData([128, 64, 192, "#8040C0"])]
    public void ToHexString_ConvertsSdColorToHex(int red, int green, int blue, string expected)
    {
        Color color = Color.FromArgb(red, green, blue);
        string result = color.ToHexString();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData([0, 0, 0, "#000000"])]
    [InlineData([255, 255, 255, "#FFFFFF"])]
    [InlineData([255, 0, 0, "#FF0000"])]
    [InlineData([0, 255, 0, "#00FF00"])]
    [InlineData([0, 0, 255, "#0000FF"])]
    [InlineData([128, 64, 192, "#8040C0"])]
    public void ToHexString_ConvertsMagickColorToHex(
        byte red,
        byte green,
        byte blue,
        string expected
    )
    {
        MagickColor color = new(red, green, blue);
        string result = color.ToHexString();
        result.Should().Be(expected);
    }

    [Fact]
    public void ToHexString_ProducesUppercaseHexDigits()
    {
        Color color = Color.FromArgb(171, 205, 239);
        string result = color.ToHexString();
        result.Should().Be("#ABCDEF");
        result.Should().StartWith("#");
        result.Length.Should().Be(7);
    }
}
