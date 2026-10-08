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

using System.Globalization;
using ImageMagick;
using NoMercy.Database;
using NoMercy.MediaProcessing.Images;

namespace NoMercy.Tests.MediaProcessing.Images;

/// <summary>
/// Pins the palette the quantizer returns for a fixed four-block image, so a
/// change of image library cannot silently shift the colors every client shows.
/// The fixture is written with Magick.NET so its bytes do not depend on the
/// library under test.
/// </summary>
[Trait("Category", "Unit")]
public class ColorQuantizerPaletteTests
{
    private const int Tolerance = 12;

    [Fact]
    public void ExtractPalette_FourSolidBlocks_ReturnsRecordedPalette()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"nomercy-quantizer-{Guid.NewGuid():N}.png"
        );

        try
        {
            WriteFourBlockPng(path);

            using MagickImage image = new(path);
            PaletteColors palette = ColorQuantizer.ExtractPalette(image);

            List<string> mismatches = [];
            Check(mismatches, "#1E3CC8FF", palette.Dominant, nameof(palette.Dominant));
            Check(mismatches, "#1E3CC8FF", palette.Primary, nameof(palette.Primary));
            Check(mismatches, "#E6C828FF", palette.LightVibrant, nameof(palette.LightVibrant));
            Check(mismatches, "#1EA028FF", palette.DarkVibrant, nameof(palette.DarkVibrant));
            Check(mismatches, "#E6C828FF", palette.LightMuted, nameof(palette.LightMuted));
            Check(mismatches, "#1EA028FF", palette.DarkMuted, nameof(palette.DarkMuted));

            Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void WriteFourBlockPng(string path)
    {
        using MagickImage canvas = new(MagickColors.Black, 128, 128);
        Composite(canvas, new MagickColor(200, 30, 30), 0, 0);
        Composite(canvas, new MagickColor(30, 160, 40), 64, 0);
        Composite(canvas, new MagickColor(30, 60, 200), 0, 64);
        Composite(canvas, new MagickColor(230, 200, 40), 64, 64);
        canvas.Write(path, MagickFormat.Png);
    }

    private static void Composite(MagickImage canvas, MagickColor color, int x, int y)
    {
        using MagickImage block = new(color, 64, 64);
        canvas.Composite(block, x, y, CompositeOperator.Over);
    }

    private static void Check(List<string> mismatches, string expected, string actual, string slot)
    {
        (int r, int g, int b) e = ParseHex(expected);
        (int r, int g, int b) a = ParseHex(actual);

        bool close =
            Math.Abs(e.r - a.r) <= Tolerance
            && Math.Abs(e.g - a.g) <= Tolerance
            && Math.Abs(e.b - a.b) <= Tolerance;

        if (!close)
            mismatches.Add($"{slot}: expected {expected} within {Tolerance} per channel, got {actual}");
    }

    private static (int r, int g, int b) ParseHex(string hex)
    {
        string digits = hex.TrimStart('#');
        return (
            int.Parse(digits[..2], NumberStyles.HexNumber),
            int.Parse(digits.Substring(2, 2), NumberStyles.HexNumber),
            int.Parse(digits.Substring(4, 2), NumberStyles.HexNumber)
        );
    }
}
