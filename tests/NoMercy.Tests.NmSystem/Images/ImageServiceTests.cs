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

using System.Text;
using ImageMagick;
using NoMercy.NmSystem.Images;

namespace NoMercy.Tests.NmSystem.Images;

[Trait("Category", "Unit")]
public class ImageServiceTests : IDisposable
{
    private const int SourceSize = 160;
    private readonly ImageService _imageService = new();
    private readonly string _sourcePath = Path.Combine(
        Path.GetTempPath(),
        $"nomercy-image-test-{Guid.NewGuid():N}.png"
    );

    public ImageServiceTests()
    {
        // High-frequency detail so encoder quality measurably affects output size;
        // a flat colour compresses identically at every quality level.
        byte[] pixels = new byte[SourceSize * SourceSize * 4];
        for (int y = 0; y < SourceSize; y++)
        for (int x = 0; x < SourceSize; x++)
        {
            int offset = (y * SourceSize + x) * 4;
            pixels[offset] = (byte)((x * 37 + y * 17) % 256);
            pixels[offset + 1] = (byte)((x * 11 + y * 53) % 256);
            pixels[offset + 2] = (byte)((x * 29 + y * 7) % 256);
            pixels[offset + 3] = 255;
        }

        PixelReadSettings settings = new(
            SourceSize,
            SourceSize,
            StorageType.Char,
            PixelMapping.RGBA
        );
        using MagickImage image = new(pixels, settings);
        image.Write(_sourcePath, MagickFormat.Png);
    }

    [Theory]
    [InlineData("png", MagickFormat.Png)]
    [InlineData("PNG", MagickFormat.Png)]
    [InlineData("jpg", MagickFormat.Jpeg)]
    [InlineData("jpeg", MagickFormat.Jpeg)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("avif", MagickFormat.Avif)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("", MagickFormat.Png)]
    [InlineData("not-a-format", MagickFormat.Png)]
    public void Parse_MapsExtensionToMagickFormat_PngWhenUnknown(
        string extension,
        MagickFormat expected
    )
    {
        _imageService.Parse(extension).Should().Be(expected);
    }

    [Theory]
    [InlineData(MagickFormat.Png, "image/png")]
    [InlineData(MagickFormat.Jpeg, "image/jpeg")]
    [InlineData(MagickFormat.WebP, "image/webp")]
    [InlineData(MagickFormat.Avif, "image/avif")]
    public void MimeType_ReturnsTheFormatsMimeType(MagickFormat format, string expected)
    {
        ImageService.MimeType(format).Should().Be(expected);
    }

    [Fact]
    public void ResizeMagickNet_KeepsSourceAspect_WhenNoAspectGiven()
    {
        (byte[] data, _) = _imageService.ResizeMagickNet(_sourcePath, 80, null, "png", null);

        using MagickImage output = new(data);
        output.Width.Should().Be(80u);
        output.Height.Should().Be(80u, "the source is square");
    }

    [Fact]
    public void ResizeMagickNet_AppliesAspectRatio()
    {
        (byte[] data, _) = _imageService.ResizeMagickNet(_sourcePath, 60, 0.5, "png", null);

        using MagickImage output = new(data);
        output.Width.Should().Be(60u);
        output.Height.Should().Be(30u);
    }

    [Fact]
    public void ResizeMagickNet_SameSizePng_KeepsPixelColors()
    {
        (byte[] data, _) = _imageService.ResizeMagickNet(
            _sourcePath,
            SourceSize,
            null,
            "png",
            null
        );

        using MagickImage source = new(_sourcePath);
        using MagickImage output = new(data);

        IMagickColor<byte> expected = source.GetPixels().GetPixel(10, 20).ToColor()!;
        IMagickColor<byte> actual = output.GetPixels().GetPixel(10, 20).ToColor()!;

        ColorDistance(expected, actual).Should().BeLessThan(8, "PNG is lossless");
    }

    [Fact]
    public void ResizeMagickNet_JpegType_ReturnsJpegBytesAndMime()
    {
        (byte[] data, string mimeType) = _imageService.ResizeMagickNet(
            _sourcePath,
            60,
            null,
            "jpg",
            80
        );

        mimeType.Should().Be("image/jpeg");
        data[0].Should().Be(0xFF);
        data[1].Should().Be(0xD8);
    }

    [Fact]
    public void ResizeMagickNet_MissingFile_Throws()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.png");

        Action act = () => _imageService.ResizeMagickNet(missing, 10, null, "png", null);

        act.Should().Throw<Exception>().WithMessage("File not found");
    }

    [Fact]
    public void MagickNet_DecodesAvif_ItWrote()
    {
        (byte[] avif, _) = _imageService.ResizeMagickNet(_sourcePath, 48, null, "avif", 60);

        using MagickImage decoded = new(avif);

        decoded.Format.Should().Be(MagickFormat.Avif);
        decoded.Width.Should().Be(48u);
        decoded.Height.Should().Be(48u);
    }

    private static double ColorDistance(IMagickColor<byte> a, IMagickColor<byte> b)
    {
        int red = a.R - b.R;
        int green = a.G - b.G;
        int blue = a.B - b.B;
        return Math.Sqrt(red * red + green * green + blue * blue);
    }

    [Fact]
    public void ResizeMagickNet_EncodesRealAvif_NotAMislabeledPng()
    {
        (byte[] data, string mimeType) = _imageService.ResizeMagickNet(
            _sourcePath,
            60,
            null,
            "avif",
            50
        );

        mimeType.Should().Be("image/avif");
        IsAvif(data).Should().BeTrue("the bytes must be a real AVIF container, not PNG");
        IsPng(data).Should().BeFalse("AVIF must not silently fall back to PNG bytes");
    }

    [Fact]
    public void ResizeMagickNet_HonorsQuality_LowerQualityIsSmaller()
    {
        (byte[] high, _) = _imageService.ResizeMagickNet(_sourcePath, 120, null, "avif", 90);
        (byte[] low, _) = _imageService.ResizeMagickNet(_sourcePath, 120, null, "avif", 20);

        low.Length.Should().BeLessThan(high.Length);
    }

    [Fact]
    public void ResizeMagickNet_PngType_ReturnsPngBytesAndMime()
    {
        (byte[] data, string mimeType) = _imageService.ResizeMagickNet(
            _sourcePath,
            60,
            null,
            "png",
            null
        );

        mimeType.Should().Be("image/png");
        IsPng(data).Should().BeTrue();
    }

    private static bool IsPng(byte[] data) =>
        data.Length >= 8
        && data[0] == 0x89
        && data[1] == 0x50
        && data[2] == 0x4E
        && data[3] == 0x47;

    private static bool IsAvif(byte[] data)
    {
        if (data.Length < 12)
            return false;

        string boxType = Encoding.ASCII.GetString(data, 4, 4);
        string majorBrand = Encoding.ASCII.GetString(data, 8, 4);

        return boxType == "ftyp" && majorBrand.Contains("avif");
    }

    public void Dispose()
    {
        if (File.Exists(_sourcePath))
            File.Delete(_sourcePath);

        GC.SuppressFinalize(this);
    }
}
