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

using ImageMagick;

namespace NoMercy.NmSystem.Images;

public class ImageService : IImageService
{
    private const int DefaultAvifQuality = 75;
    private const string DefaultMimeType = "image/png";

    // File extensions whose Magick.NET format name is not the extension itself.
    private static readonly Dictionary<string, MagickFormat> ExtensionAliases = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["jpg"] = MagickFormat.Jpeg,
        ["jpeg"] = MagickFormat.Jpeg,
        ["tif"] = MagickFormat.Tiff,
    };

    public MagickFormat Parse(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return MagickFormat.Png;

        format = format.Trim().TrimStart('.');

        if (ExtensionAliases.TryGetValue(format, out MagickFormat alias))
            return alias;

        // Enum.TryParse also accepts numeric strings ("3"); only names are formats.
        if (
            !char.IsDigit(format[0])
            && Enum.TryParse(format, ignoreCase: true, out MagickFormat parsed)
            && Enum.IsDefined(parsed)
        )
            return parsed;

        return MagickFormat.Png;
    }

    public static string MimeType(MagickFormat format)
    {
        IMagickFormatInfo? info = MagickFormatInfo.Create(format);
        return string.IsNullOrEmpty(info?.MimeType) ? DefaultMimeType : info.MimeType;
    }

    public (byte[] data, string mimeType) ResizeMagickNet(
        string image,
        int? width,
        double? aspectRatio,
        string? type,
        int? quality
    )
    {
        if (!File.Exists(image))
            throw new("File not found");

        MagickFormat format = Parse(type ?? "png");

        using MagickImage magick = new(image);

        (int targetWidth, int targetHeight) = TargetSize(
            (int)magick.Width,
            (int)magick.Height,
            width,
            aspectRatio
        );

        MagickGeometry geometry = new((uint)targetWidth, (uint)targetHeight)
        {
            IgnoreAspectRatio = true,
        };
        magick.Resize(geometry);

        magick.Format = format;

        int? effectiveQuality =
            quality ?? (format is MagickFormat.Avif ? DefaultAvifQuality : null);
        if (effectiveQuality is not null)
            magick.Quality = (uint)Math.Clamp(effectiveQuality.Value, 1, 100);

        return (magick.ToByteArray(), MimeType(format));
    }

    private static (int width, int height) TargetSize(
        int sourceWidth,
        int sourceHeight,
        int? width,
        double? aspectRatio
    )
    {
        double ratio = aspectRatio ?? sourceHeight / (double)sourceWidth;
        int targetWidth = width ?? sourceWidth;
        int targetHeight = (int)(targetWidth * ratio);

        return (targetWidth, targetHeight);
    }
}
