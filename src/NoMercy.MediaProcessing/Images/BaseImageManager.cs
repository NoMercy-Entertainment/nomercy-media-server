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
using Newtonsoft.Json;
using NoMercy.Database;

namespace NoMercy.MediaProcessing.Images;

public class BaseImageManager : IBaseImageManager, IDisposable
{
    public delegate Task<MagickImage?> DownloadUrl(
        Uri path,
        bool? download,
        Size? maxDecodeSize = null
    );

    public delegate Task<MagickImage?>? DownloadPath(
        string? path,
        bool? download,
        Size? maxDecodeSize = null
    );

    public class ColorPaletteArgument
    {
        public required string Key { get; set; }
        public MagickImage? ImageData { get; set; }
    }

    public class MultiUriType(string key, Uri url)
    {
        public readonly string Key = key;
        public readonly Uri Url = url;
    }

    public class MultiStringType(string key, string? path)
    {
        public readonly string Key = key;
        public readonly string? Path = path;
    }

    public static string GenerateColorPalette(IEnumerable<ColorPaletteArgument> items)
    {
        // A null image has no palette: omit its key, since an all-null entry
        // serializes as {} and reads as answered forever.
        Dictionary<string, PaletteColors?> dict = new();

        foreach (ColorPaletteArgument item in items)
            if (item.ImageData is not null)
                dict.Add(item.Key, ColorPaletteFromImage(item.ImageData));

        return JsonConvert.SerializeObject(dict);
    }

    private static HttpRequestException DownloadFailed(IEnumerable<string?> paths) =>
        new(
            "Palette image download failed (rate limit, server error or undecodable body): "
                + string.Join(", ", paths)
        );

    public static PaletteColors ColorPaletteFromImage(MagickImage? image)
    {
        if (image is null)
            return new()
            {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
                Dominant = null,
                Primary = null,
                LightVibrant = null,
                DarkVibrant = null,
                LightMuted = null,
                DarkMuted = null
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
            };

        return GetColorPaletteColors(image);
    }

    public static async Task<string> ColorPalette(
        DownloadUrl client,
        string type,
        Uri path,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        MagickImage? imageData = await client.Invoke(path, download, maxDecodeSize);
        if (imageData == null)
            return "";

        return GenerateColorPalette(
            new List<ColorPaletteArgument>
            {
                new() { Key = type, ImageData = imageData },
            }
        );
    }

    public static async Task<string> MultiColorPalette(
        DownloadUrl client,
        IEnumerable<MultiUriType> items,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        List<ColorPaletteArgument> list = [];
        foreach (MultiUriType item in items)
        {
            MagickImage? imageData = await client.Invoke(item.Url, download, maxDecodeSize);
            list.Add(new() { Key = item.Key, ImageData = imageData });
        }

        return GenerateColorPalette(list);
    }

    public static async Task<string> ColorPalette(
        DownloadPath client,
        string type,
        string? path,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        MagickImage? imageData = await client.Invoke(path, download, maxDecodeSize);
#pragma warning restore CS8602 // Dereference of a possibly null reference.

        // A requested path whose download returned null is a failed download,
        // not an entity without an image: throw so the queue retries it.
        if (imageData is null && !string.IsNullOrEmpty(path))
            throw DownloadFailed([path]);

        return GenerateColorPalette(
            new List<ColorPaletteArgument>
            {
                new() { Key = type, ImageData = imageData },
            }
        );
    }

    public static async Task<string> MultiColorPalette(
        DownloadPath client,
        IEnumerable<MultiStringType> items,
        bool? download = true,
        Size? maxDecodeSize = null
    )
    {
        List<ColorPaletteArgument> list = [];
        List<string> failed = [];
        foreach (MultiStringType item in items)
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            MagickImage? imageData = await client.Invoke(item.Path, download, maxDecodeSize);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            list.Add(new() { Key = item.Key, ImageData = imageData });
            if (imageData is null && !string.IsNullOrEmpty(item.Path))
                failed.Add(item.Path);
        }

        // Every requested image failed: throw so the queue retries; a partial result is kept.
        if (failed.Count > 0 && list.All(x => x.ImageData is null))
            throw DownloadFailed(failed);

        return GenerateColorPalette(list);
    }

    public void Dispose() { }

    public static PaletteColors GetColorPaletteColors(MagickImage image)
    {
        return ColorQuantizer.ExtractPalette(image);
    }
}
