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
        Dictionary<string, PaletteColors?> palette = new();

        foreach (ColorPaletteArgument item in items)
            palette.Add(item.Key, ColorPaletteFromImage(item.ImageData));

        IEnumerable<KeyValuePair<string, PaletteColors?>> palettes = palette.Where(x =>
            x.Value != null
        );

        Dictionary<string, PaletteColors?> dict = palettes.ToDictionary(x => x.Key, x => x.Value);

        return JsonConvert.SerializeObject(dict);
    }

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
        foreach (MultiStringType item in items)
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            MagickImage? imageData = await client.Invoke(item.Path, download, maxDecodeSize);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            list.Add(new() { Key = item.Key, ImageData = imageData });
        }

        return GenerateColorPalette(list);
    }

    public void Dispose() { }

    public static PaletteColors GetColorPaletteColors(MagickImage image)
    {
        return ColorQuantizer.ExtractPalette(image);
    }
}
